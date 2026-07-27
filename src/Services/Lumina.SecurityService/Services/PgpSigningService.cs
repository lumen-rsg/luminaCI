using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Lumina.SecurityService.Data;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SecurityService.Services;

public class PgpSigningService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ArtifactLocks = new();

    private readonly SecurityDbContext _db;
    private readonly ILogger<PgpSigningService> _logger;
    private readonly string _artifactsRoot;
    private readonly string _keyDirectory;
    private readonly string _passphraseFile;

    public PgpSigningService(
        SecurityDbContext db,
        ILogger<PgpSigningService> logger,
        IConfiguration config)
    {
        _db = db;
        _logger = logger;
        _artifactsRoot = config["Builds:ArtifactsRoot"] ?? "/app/builds";
        _keyDirectory = config["Gpg:KeyDirectory"] ?? "/app/keys";
        _passphraseFile = config["Gpg:PassphraseFile"]
            ?? throw new InvalidOperationException(
                "Gpg:PassphraseFile is not configured. Mount the RPM signing passphrase as a secret file.");
    }

    public async Task<SecurityKey> GenerateKeyAsync(string keyName, string email, string createdBy)
    {
        var safeKeyName = ProcessArgumentSanitizer.ValidateKeyName(keyName);
        var safeEmail = ProcessArgumentSanitizer.ValidateEmail(email);
        EnsurePassphraseFile();
        Directory.CreateDirectory(_keyDirectory);

        var uid = $"{safeKeyName} <{safeEmail}>";
        var existingFingerprints = (await ListSecretKeyFingerprintsAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generate = await RunProcessAsync("gpg", args =>
        {
            args.Add("--batch");
            args.Add("--pinentry-mode");
            args.Add("loopback");
            args.Add("--passphrase-file");
            args.Add(_passphraseFile);
            args.Add("--quick-generate-key");
            args.Add(uid);
            args.Add("rsa4096");
            args.Add("sign");
            args.Add("0");
        });
        EnsureSuccess(generate, "GPG key generation");

        var fingerprint = (await ListSecretKeyFingerprintsAsync())
            .SingleOrDefault(candidate => !existingFingerprints.Contains(candidate))
            ?? throw new InvalidOperationException("GPG did not expose the newly generated key fingerprint.");
        var export = await RunProcessAsync("gpg", args =>
        {
            args.Add("--batch");
            args.Add("--armor");
            args.Add("--export");
            args.Add(fingerprint);
        });
        EnsureSuccess(export, "GPG public-key export");
        if (string.IsNullOrWhiteSpace(export.StandardOutput))
            throw new InvalidOperationException("GPG public-key export returned no key data.");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.SecurityKeys
            .Where(k => k.IsActive)
            .ExecuteUpdateAsync(setters => setters.SetProperty(k => k.IsActive, false));

        var key = new SecurityKey
        {
            Id = Guid.NewGuid(),
            KeyId = fingerprint,
            KeyName = safeKeyName,
            PublicKey = export.StandardOutput,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
        _db.SecurityKeys.Add(key);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        _logger.LogInformation("RPM signing key {Fingerprint} generated and activated for {KeyName}", fingerprint, safeKeyName);
        return key;
    }

    public async Task ReconcileLegacyKeyFingerprintsAsync()
    {
        var keys = await _db.SecurityKeys.ToListAsync();
        foreach (var key in keys.Where(k => !IsFingerprint(k.KeyId)))
        {
            var keyFile = Path.Combine(_keyDirectory, $"reconcile-{Guid.NewGuid():N}.asc");
            Directory.CreateDirectory(_keyDirectory);
            await File.WriteAllTextAsync(keyFile, key.PublicKey);
            try
            {
                var inspect = await RunProcessAsync("gpg", args =>
                {
                    args.Add("--batch");
                    args.Add("--with-colons");
                    args.Add("--import-options");
                    args.Add("show-only");
                    args.Add("--import");
                    args.Add(keyFile);
                });
                EnsureSuccess(inspect, "legacy GPG fingerprint inspection");
                key.KeyId = ExtractFingerprint(inspect.StandardOutput);
            }
            finally
            {
                File.Delete(keyFile);
            }
        }

        if (_db.ChangeTracker.HasChanges())
        {
            await _db.SaveChangesAsync();
        }
    }

    public async Task<SigningRequest> SignArtifactAsync(
        Guid artifactId,
        string artifactPath,
        string expectedSha256,
        Guid keyId)
    {
        ValidateSha256(expectedSha256);
        var gate = ArtifactLocks.GetOrAdd(artifactId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            var safeArtifactPath = ProcessArgumentSanitizer.ResolveConfinedPath(artifactPath, _artifactsRoot);
            if (!string.Equals(Path.GetExtension(safeArtifactPath), ".rpm", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only RPM artifacts can be signed.");
            if (!File.Exists(safeArtifactPath))
                throw new FileNotFoundException("RPM artifact not found.", safeArtifactPath);

            var key = await _db.SecurityKeys.SingleOrDefaultAsync(k => k.Id == keyId)
                ?? throw new InvalidOperationException($"Key {keyId} not found.");
            if (!key.IsActive)
                throw new InvalidOperationException($"Key {keyId} is not active.");

            var request = await _db.SigningRequests.SingleOrDefaultAsync(r => r.ArtifactId == artifactId);
            if (request?.Status == "Signed")
            {
                if (!string.Equals(request.ExpectedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The signing request digest does not match the completed signing record.");

                var currentHash = await ComputeSha256Async(safeArtifactPath);
                if (!string.Equals(currentHash, request.SignedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The signed RPM changed after signing.");
                return request;
            }

            request ??= new SigningRequest
            {
                Id = Guid.NewGuid(),
                ArtifactId = artifactId,
                ArtifactPath = safeArtifactPath,
                KeyId = key.Id,
                KeyFingerprint = key.KeyId,
                ExpectedSha256 = expectedSha256,
                CreatedAt = DateTime.UtcNow
            };
            request.ArtifactPath = safeArtifactPath;
            request.KeyId = key.Id;
            request.KeyFingerprint = key.KeyId;
            request.ExpectedSha256 = expectedSha256;
            request.Status = "Pending";
            request.Error = null;
            request.CompletedAt = null;
            request.SignedSha256 = null;
            request.SignedFileSize = null;
            if (_db.Entry(request).State == EntityState.Detached)
                _db.SigningRequests.Add(request);
            await _db.SaveChangesAsync();

            EnsurePassphraseFile();
            var signingDirectory = Path.Combine(
                Path.GetTempPath(), "lumina-signing", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(signingDirectory);
            var tempPath = Path.Combine(signingDirectory, Path.GetFileName(safeArtifactPath));
            var replacementPath = Path.Combine(
                Path.GetDirectoryName(safeArtifactPath)!,
                $".{Path.GetFileNameWithoutExtension(safeArtifactPath)}.signed-{Guid.NewGuid():N}.rpm");
            var verificationDirectory = Path.Combine(_keyDirectory, $"verify-{Guid.NewGuid():N}");

            try
            {
                File.Copy(safeArtifactPath, tempPath, overwrite: false);
                var snapshotSha256 = await ComputeSha256Async(tempPath);
                if (!string.Equals(snapshotSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Artifact digest mismatch before signing: expected {expectedSha256}, got {snapshotSha256}.");

                var sign = await RunProcessAsync("rpmsign", args =>
                {
                    args.Add("--define");
                    args.Add($"_gpg_path {Environment.GetEnvironmentVariable("GNUPGHOME") ?? "/app/.gnupg"}");
                    args.Add("--define");
                    args.Add($"_gpg_name {key.KeyId}");
                    args.Add("--define");
                    args.Add($"_gpg_sign_cmd_extra_args --batch --pinentry-mode loopback --passphrase-file {_passphraseFile}");
                    args.Add("--resign");
                    args.Add(tempPath);
                });
                EnsureSuccess(sign, "RPM signing");

                await VerifyEmbeddedSignatureAsync(tempPath, key.PublicKey, verificationDirectory);

                request.SignedSha256 = await ComputeSha256Async(tempPath);
                request.SignedFileSize = new FileInfo(tempPath).Length;
                File.Copy(tempPath, replacementPath, overwrite: false);
                var replacementSha256 = await ComputeSha256Async(replacementPath);
                if (!string.Equals(
                        replacementSha256,
                        request.SignedSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Signed RPM changed while preparing atomic replacement.");
                }
                File.Move(replacementPath, safeArtifactPath, overwrite: true);

                request.Status = "Signed";
                request.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                _logger.LogInformation(
                    "RPM artifact {ArtifactId} signed and verified with key {Fingerprint}",
                    artifactId, key.KeyId);
                return request;
            }
            finally
            {
                if (Directory.Exists(signingDirectory))
                    Directory.Delete(signingDirectory, recursive: true);
                if (File.Exists(replacementPath))
                    File.Delete(replacementPath);
                if (Directory.Exists(verificationDirectory))
                    Directory.Delete(verificationDirectory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            var request = await _db.SigningRequests.SingleOrDefaultAsync(r => r.ArtifactId == artifactId);
            if (request is not null && request.Status != "Signed")
            {
                request.Status = "Failed";
                request.Error = ex.Message;
                request.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }

            _logger.LogError(ex, "Failed to sign RPM artifact {ArtifactId}", artifactId);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SecurityKey?> GetActiveKeyAsync() =>
        await _db.SecurityKeys.SingleOrDefaultAsync(k => k.IsActive);

    public async Task<List<SecurityKey>> ListKeysAsync() =>
        await _db.SecurityKeys.OrderByDescending(k => k.CreatedAt).ToListAsync();

    public async Task<List<SigningRequest>> GetRecentSigningsAsync(int count = 50) =>
        await _db.SigningRequests.OrderByDescending(s => s.CreatedAt).Take(count).ToListAsync();

    private async Task VerifyEmbeddedSignatureAsync(string rpmPath, string publicKey, string verificationDirectory)
    {
        Directory.CreateDirectory(verificationDirectory);
        var publicKeyPath = Path.Combine(verificationDirectory, "signing-key.asc");
        await File.WriteAllTextAsync(publicKeyPath, publicKey);

        var import = await RunProcessAsync("rpmkeys", args =>
        {
            args.Add("--dbpath");
            args.Add(verificationDirectory);
            args.Add("--import");
            args.Add(publicKeyPath);
        });
        EnsureSuccess(import, "RPM verification-key import");

        var verify = await RunProcessAsync("rpmkeys", args =>
        {
            args.Add("--dbpath");
            args.Add(verificationDirectory);
            args.Add("--checksig");
            args.Add(rpmPath);
        });
        EnsureSuccess(verify, "RPM signature verification");
        var output = $"{verify.StandardOutput}\n{verify.StandardError}";
        if (!output.Contains("signatures OK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"RPM signature verification did not report a valid signature: {output.Trim()}");
    }

    private async Task<List<string>> ListSecretKeyFingerprintsAsync()
    {
        var result = await RunProcessAsync("gpg", args =>
        {
            args.Add("--batch");
            args.Add("--with-colons");
            args.Add("--fingerprint");
            args.Add("--list-secret-keys");
        });
        EnsureSuccess(result, "GPG fingerprint lookup");

        return ExtractFingerprints(result.StandardOutput);
    }

    private static string ExtractFingerprint(string colonOutput)
    {
        var fingerprint = ExtractFingerprints(colonOutput).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new InvalidOperationException("GPG did not return a full fingerprint.");
        return fingerprint;
    }

    private static List<string> ExtractFingerprints(string colonOutput)
    {
        return colonOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':'))
            .Where(fields => fields.Length > 9 && fields[0] == "fpr")
            .Select(fields => fields[9])
            .Where(fingerprint => !string.IsNullOrWhiteSpace(fingerprint))
            .Select(fingerprint => fingerprint.ToUpperInvariant())
            .ToList();
    }

    private static bool IsFingerprint(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private void EnsurePassphraseFile()
    {
        if (!Path.IsPathFullyQualified(_passphraseFile) || !File.Exists(_passphraseFile))
            throw new InvalidOperationException("The configured GPG passphrase secret file is missing.");
        if (new FileInfo(_passphraseFile).Length == 0)
            throw new InvalidOperationException("The configured GPG passphrase secret file is empty.");
    }

    private static void ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("ExpectedSha256 must be a 64-character hexadecimal SHA-256 digest.", nameof(value));
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, Action<List<string>> configure)
    {
        var arguments = new List<string>();
        configure(arguments);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"{operation} failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
