using System.Diagnostics;
using Lumina.SecurityService.Data;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SecurityService.Services;

public class PgpSigningService
{
    private readonly SecurityDbContext _db;
    private readonly ILogger<PgpSigningService> _logger;
    private readonly IConfiguration _config;

    public PgpSigningService(SecurityDbContext db, ILogger<PgpSigningService> logger, IConfiguration config)
    {
        _db = db;
        _logger = logger;
        _config = config;
    }

    public async Task<SecurityKey> GenerateKeyAsync(string keyName, string email, string passphrase, string createdBy)
    {
        // SECURITY: Sanitize all user inputs to prevent GPG batch script injection
        var safeKeyName = ProcessArgumentSanitizer.SanitizeGpgField(keyName, nameof(keyName));
        var safeEmail = ProcessArgumentSanitizer.SanitizeGpgField(email, nameof(email));
        var safePassphrase = ProcessArgumentSanitizer.SanitizeGpgField(passphrase, nameof(passphrase));

        var keyId = Guid.NewGuid().ToString("N")[..16];
        var keyDir = _config["Gpg:KeyDirectory"] ?? "/app/keys";
        Directory.CreateDirectory(keyDir);

        var keyFilePath = Path.Combine(keyDir, $"lumina-{keyId}");

        // GPG batch script — sanitized inputs prevent injection
        var batchScript = $@"
%echo Generating PGP key for Lumina CI
Key-Type: RSA
Key-Length: 4096
Subkey-Type: RSA
Subkey-Length: 2048
Name-Real: {safeKeyName}
Name-Email: {safeEmail}
Expire-Date: 0
Passphrase: {safePassphrase}
%commit
%echo Done
";
        var batchFile = Path.Combine(keyDir, $"batch-{keyId}");
        await File.WriteAllTextAsync(batchFile, batchScript);

        try
        {
            // SECURITY: Use ArgumentList instead of string concatenation
            var startInfo = new ProcessStartInfo
            {
                FileName = "gpg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--batch");
            startInfo.ArgumentList.Add("--pinentry-mode");
            startInfo.ArgumentList.Add("loopback");
            startInfo.ArgumentList.Add("--generate-key");
            startInfo.ArgumentList.Add(batchFile);

            using var process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("Failed to start gpg process");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"GPG key generation failed: {error}");
            }

            // Export public key
            var exportPubInfo = new ProcessStartInfo
            {
                FileName = "gpg",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            exportPubInfo.ArgumentList.Add("--armor");
            exportPubInfo.ArgumentList.Add("--export");
            exportPubInfo.ArgumentList.Add(safeEmail);

            using var exportProcess = Process.Start(exportPubInfo);
            if (exportProcess == null) throw new InvalidOperationException("Failed to export public key");
            var publicKey = await exportProcess.StandardOutput.ReadToEndAsync();
            await exportProcess.WaitForExitAsync();

            var key = new SecurityKey
            {
                Id = Guid.NewGuid(),
                KeyId = keyId,
                KeyName = safeKeyName,
                PublicKey = publicKey,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = createdBy
            };

            _db.SecurityKeys.Add(key);
            await _db.SaveChangesAsync();

            _logger.LogInformation("PGP key {KeyId} generated for {KeyName}", keyId, safeKeyName);
            return key;
        }
        finally
        {
            if (File.Exists(batchFile)) File.Delete(batchFile);
        }
    }

    public async Task<SigningRequest> SignArtifactAsync(Guid artifactId, string artifactPath, Guid keyId)
    {
        var key = await _db.SecurityKeys.FindAsync(keyId);
        if (key == null) throw new InvalidOperationException($"Key {keyId} not found");
        if (!key.IsActive) throw new InvalidOperationException($"Key {keyId} is not active");

        // SECURITY: Sanitize file paths to prevent command injection
        var safeArtifactPath = ProcessArgumentSanitizer.SanitizeFilePath(artifactPath);
        var signaturePath = artifactPath + ".sig";
        var safeSignaturePath = ProcessArgumentSanitizer.SanitizeFilePath(signaturePath);

        var request = new SigningRequest
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifactId,
            ArtifactPath = artifactPath,
            SignaturePath = signaturePath,
            KeyId = keyId,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            // SECURITY: Use ArgumentList instead of string concatenation
            var startInfo = new ProcessStartInfo
            {
                FileName = "gpg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--detach-sign");
            startInfo.ArgumentList.Add("--armor");
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(signaturePath); // Original path for filesystem operation
            startInfo.ArgumentList.Add(artifactPath);   // Original path for filesystem operation

            using var process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("Failed to start gpg process");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                request.Status = "Failed";
                request.Error = error;
                request.CompletedAt = DateTime.UtcNow;
                _db.SigningRequests.Add(request);
                await _db.SaveChangesAsync();
                throw new InvalidOperationException($"Signing failed: {error}");
            }

            request.Status = "Signed";
            request.CompletedAt = DateTime.UtcNow;
            _db.SigningRequests.Add(request);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Artifact {ArtifactPath} signed with key {KeyId}", artifactPath, keyId);
            return request;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sign artifact {ArtifactPath}", artifactPath);
            throw;
        }
    }

    public async Task<List<SecurityKey>> ListKeysAsync()
    {
        return await _db.SecurityKeys.OrderByDescending(k => k.CreatedAt).ToListAsync();
    }

    public async Task<List<SigningRequest>> GetRecentSigningsAsync(int count = 50)
    {
        return await _db.SigningRequests.OrderByDescending(s => s.CreatedAt).Take(count).ToListAsync();
    }
}