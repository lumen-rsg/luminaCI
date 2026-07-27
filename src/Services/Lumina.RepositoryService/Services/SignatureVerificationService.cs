using System.Diagnostics;
using Lumina.Shared.Errors;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.RepositoryService.Services;

/// <summary>
/// Verifies a detached PGP signature (<c>.asc</c>) against an uploaded RPM
/// using the active public key fetched from SecurityService over the message
/// bus. RepositoryService runs in its own container with its own transient
/// keyring and shares no filesystem with SecurityService, so it cannot reuse
/// SecurityService's keyring — it imports the active armored public key into a
/// per-call temporary keyring, then runs <c>gpg --verify</c>.
/// </summary>
/// <remarks>
/// <b>v1 limitation:</b> verifies against the <i>active</i> key only. An
/// upload signed by a valid-but-deactivated key is rejected. Importing all
/// known keys (to support rotation) is a follow-up.
/// </remarks>
public class SignatureVerificationService
{
    private readonly IBus _bus;
    private readonly ILogger<SignatureVerificationService> _logger;

    public SignatureVerificationService(IBus bus, ILogger<SignatureVerificationService> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    /// <summary>
    /// Verifies that <paramref name="signatureStream"/> is a valid detached PGP
    /// signature for <paramref name="rpmStream"/>, against the active public key.
    /// Returns the armored signature content on success (for storage on the
    /// published Package). Throws <see cref="ValidationException"/> on any
    /// verification failure (no active key, unreachable SecurityService, or a
    /// signature that does not validate).
    /// </summary>
    public async Task<string> VerifyAsync(Stream rpmStream, Stream signatureStream, string rpmFileNameForLogs)
    {
        // 1. Fetch the active public key. No key -> uploads cannot be verified.
        string publicKeyArmored;
        try
        {
            var response = await _bus.Request<GetActivePublicKey, ActivePublicKey>(
                new GetActivePublicKey(), timeout: TimeSpan.FromSeconds(10));
            publicKeyArmored = response.Message.PublicKeyArmored ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch active public key from SecurityService via bus");
            throw new ValidationException(
                "Could not retrieve the active PGP public key (SecurityService unreachable). Cannot verify the uploaded signature.");
        }

        if (string.IsNullOrWhiteSpace(publicKeyArmored))
        {
            throw new ValidationException(
                "No active PGP key — cannot verify uploaded packages. Generate a key in Security settings first.");
        }

        // 2. Per-call temp dir + keyring. Fixed filenames (not the uploaded file
        //    name) so there is no path/argument-injection surface from the
        //    caller-controlled original name. GUID suffix prevents collisions.
        var tempDir = Path.Combine(Path.GetTempPath(), "lumina-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var rpmPath = Path.Combine(tempDir, "pkg.rpm");
        var sigPath = Path.Combine(tempDir, "pkg.rpm.asc");
        var pubKeyPath = Path.Combine(tempDir, "pubkey.asc");

        try
        {
            await File.WriteAllBytesAsync(rpmPath, await ToArrayAsync(rpmStream));
            await File.WriteAllTextAsync(sigPath, await new StreamReader(signatureStream).ReadToEndAsync());
            await File.WriteAllTextAsync(pubKeyPath, publicKeyArmored);

            // 3. Import the active public key into the transient keyring.
            var importResult = await RunGpgAsync(tempDir, args =>
            {
                args.Add("--import");
                args.Add(pubKeyPath);
            });
            if (!importResult.Success)
            {
                // Log the gpg stderr server-side only; never surface driver/tool
                // output to the client.
                _logger.LogWarning("Failed to import active public key into verification keyring: {Error}", importResult.Error);
                throw new ValidationException(
                    "Could not import the active PGP public key into the verification keyring.");
            }

            // 4. Verify the detached signature against the RPM.
            var verifyResult = await RunGpgAsync(tempDir, args =>
            {
                args.Add("--verify");
                args.Add(sigPath);
                args.Add(rpmPath);
            });
            if (!verifyResult.Success)
            {
                _logger.LogWarning("Signature verification failed for uploaded RPM '{File}': {Error}", rpmFileNameForLogs, verifyResult.Error);
                throw new ValidationException(
                    "Signature verification failed: the detached signature does not match the uploaded RPM, or it was not produced by the active key.");
            }

            // Return the armored signature so it can be stored on the Package.
            var signature = await File.ReadAllTextAsync(sigPath);
            _logger.LogInformation("Verified detached signature for uploaded RPM '{File}'", rpmFileNameForLogs);
            return signature;
        }
        finally
        {
            // Best-effort cleanup of the transient keyring + inputs. Never let a
            // cleanup failure mask the real error.
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up verification temp dir {Dir}", tempDir); }
        }
    }

    /// <summary>
    /// Verifies an RPM's embedded signature against the trusted key selected by
    /// full fingerprint. The keyring and RPM database are private per call.
    /// </summary>
    public async Task VerifyEmbeddedRpmAsync(string rpmPath, string expectedFingerprint)
    {
        var fingerprint = expectedFingerprint.Trim().ToUpperInvariant();
        if (fingerprint.Length is not (40 or 64) || !fingerprint.All(Uri.IsHexDigit))
            throw new ValidationException("Signing metadata contains an invalid key fingerprint.");

        string publicKeyArmored;
        try
        {
            var response = await _bus.Request<GetPublicKey, PublicKeyByFingerprint>(
                new GetPublicKey(fingerprint), timeout: TimeSpan.FromSeconds(10));
            publicKeyArmored = response.Message.PublicKeyArmored ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch trusted public key {Fingerprint}", fingerprint);
            throw new ValidationException("Could not retrieve the trusted RPM signing key.");
        }

        if (string.IsNullOrWhiteSpace(publicKeyArmored))
            throw new ValidationException($"RPM signing key {fingerprint} is not trusted.");

        var tempDir = Path.Combine(Path.GetTempPath(), "lumina-rpmkeys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var pubKeyPath = Path.Combine(tempDir, "pubkey.asc");
        try
        {
            await File.WriteAllTextAsync(pubKeyPath, publicKeyArmored);
            var import = await RunProcessAsync("rpmkeys", args =>
            {
                args.Add("--dbpath");
                args.Add(tempDir);
                args.Add("--import");
                args.Add(pubKeyPath);
            });
            if (!import.Success)
            {
                _logger.LogWarning("rpmkeys could not import trusted key {Fingerprint}: {Error}", fingerprint, import.Error);
                throw new ValidationException("Could not initialize the RPM signature verification keyring.");
            }

            var verify = await RunProcessAsync("rpmkeys", args =>
            {
                args.Add("--dbpath");
                args.Add(tempDir);
                args.Add("--checksig");
                args.Add(rpmPath);
            });
            var output = $"{verify.Output}\n{verify.Error}";
            if (!verify.Success || !output.Contains("signatures OK", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Embedded RPM signature verification failed for {RpmPath}: {Output}",
                    rpmPath, output.Trim());
                throw new ValidationException(
                    "The RPM does not contain a valid embedded signature from the expected trusted key.");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up RPM verification directory {Dir}", tempDir); }
        }
    }

    private static async Task<byte[]> ToArrayAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Runs <c>gpg</c> with a transient <c>--homedir</c> and an ArgumentList
    /// (never a shell string), mirroring the process pattern in
    /// <c>PgpSigningService</c>. Returns (success, stderr).
    /// </summary>
    private async Task<(bool Success, string Error)> RunGpgAsync(string homeDir, Action<List<string>> configureArgs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gpg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--homedir");
        startInfo.ArgumentList.Add(homeDir);
        startInfo.ArgumentList.Add("--batch");
        startInfo.ArgumentList.Add("--yes");
        // Suppress gpg's trust-db creation noise / interactive prompts in the
        // transient keyring; we only need signature validity.
        startInfo.ArgumentList.Add("--no-tty");

        var extra = new List<string>();
        configureArgs(extra);
        foreach (var a in extra) startInfo.ArgumentList.Add(a);

        using var process = Process.Start(startInfo);
        if (process is null)
            return (false, "Failed to start gpg process");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        return (process.ExitCode == 0, await stderr);
    }

    private static async Task<(bool Success, string Output, string Error)> RunProcessAsync(
        string fileName,
        Action<List<string>> configureArgs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var args = new List<string>();
        configureArgs(args);
        foreach (var argument in args)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
            return (false, "", $"Failed to start {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode == 0, await stdout, await stderr);
    }
}
