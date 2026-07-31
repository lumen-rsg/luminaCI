using System.Diagnostics;

namespace Lumina.BuildService.Services;

/// <summary>
/// Uses RPM's own package reader to validate the package header and required
/// NEVRA fields. Arguments are passed without a shell.
/// </summary>
public sealed class RpmArtifactValidator : IRpmArtifactValidator
{
    private static readonly byte[] RpmLeadMagic = [0xed, 0xab, 0xee, 0xdb];
    private readonly string _rpmExecutable;
    private readonly TimeSpan _timeout;
    private readonly ILogger<RpmArtifactValidator> _logger;

    public RpmArtifactValidator(IConfiguration configuration, ILogger<RpmArtifactValidator> logger)
    {
        _rpmExecutable = configuration["Rpm:Executable"] ?? "rpm";
        _timeout = TimeSpan.FromSeconds(
            int.TryParse(configuration["Rpm:ValidationTimeoutSeconds"], out var seconds) && seconds > 0
                ? seconds
                : 30);
        _logger = logger;
    }

    public async Task<RpmValidationResult> ValidateAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return RpmValidationResult.Invalid("file does not exist");

        try
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length < RpmLeadMagic.Length)
                return RpmValidationResult.Invalid("file is too short to be an RPM");

            var magic = new byte[RpmLeadMagic.Length];
            var bytesRead = await stream.ReadAsync(magic, cancellationToken);
            if (bytesRead != magic.Length || !magic.AsSpan().SequenceEqual(RpmLeadMagic))
                return RpmValidationResult.Invalid("RPM lead magic is invalid");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RpmValidationResult.Invalid($"file cannot be read: {ex.Message}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _rpmExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--query");
        startInfo.ArgumentList.Add("--package");
        startInfo.ArgumentList.Add("--queryformat");
        startInfo.ArgumentList.Add(
            "%{NAME}-%{EPOCHNUM}:%{VERSION}-%{RELEASE}.%{ARCH}\t%{ARCH}\t%{NAME}-%{VERSION}-%{RELEASE}.%{ARCH}.rpm");
        startInfo.ArgumentList.Add(path);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return RpmValidationResult.Invalid($"rpm validation exceeded {_timeout.TotalSeconds:0} seconds");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = (await stdoutTask).Trim();
            var stderr = Truncate((await stderrTask).Trim(), 1_000);
            if (process.ExitCode != 0)
                return RpmValidationResult.Invalid(
                    string.IsNullOrWhiteSpace(stderr) ? $"rpm exited with code {process.ExitCode}" : stderr);

            var fields = stdout.Split('\t', StringSplitOptions.TrimEntries);
            if (fields.Length != 3 ||
                fields.Any(string.IsNullOrWhiteSpace) ||
                fields[0].Contains("(none)", StringComparison.OrdinalIgnoreCase) ||
                fields[1].Contains("(none)", StringComparison.OrdinalIgnoreCase) ||
                fields[2].Contains("(none)", StringComparison.OrdinalIgnoreCase))
            {
                return RpmValidationResult.Invalid("RPM is missing required NEVRA metadata");
            }

            return RpmValidationResult.Valid(fields[0], fields[1], fields[2]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "Unable to execute RPM validator {Executable}", _rpmExecutable);
            throw new InvalidOperationException(
                $"RPM validation executable '{_rpmExecutable}' is unavailable", ex);
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between HasExited and Kill.
        }
    }
}
