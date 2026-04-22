using System.Diagnostics;
using Lumina.SecurityService.Data;
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
        var keyId = Guid.NewGuid().ToString("N")[..16];
        var keyDir = _config["Gpg:KeyDirectory"] ?? "/app/keys";
        Directory.CreateDirectory(keyDir);

        var keyFilePath = Path.Combine(keyDir, $"lumina-{keyId}");

        // Generate PGP key using gpg command
        var batchScript = $@"
%echo Generating PGP key for Lumina CI
Key-Type: RSA
Key-Length: 4096
Subkey-Type: RSA
Subkey-Length: 2048
Name-Real: {keyName}
Name-Email: {email}
Expire-Date: 0
Passphrase: {passphrase}
%commit
%echo Done
";
        var batchFile = Path.Combine(keyDir, $"batch-{keyId}");
        await File.WriteAllTextAsync(batchFile, batchScript);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "gpg",
                Arguments = $"--batch --pinentry-mode loopback --generate-key {batchFile}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

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
                Arguments = $"--armor --export {email}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var exportProcess = Process.Start(exportPubInfo);
            if (exportProcess == null) throw new InvalidOperationException("Failed to export public key");
            var publicKey = await exportProcess.StandardOutput.ReadToEndAsync();
            await exportProcess.WaitForExitAsync();

            var key = new SecurityKey
            {
                Id = Guid.NewGuid(),
                KeyId = keyId,
                KeyName = keyName,
                PublicKey = publicKey,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = createdBy
            };

            _db.SecurityKeys.Add(key);
            await _db.SaveChangesAsync();

            _logger.LogInformation("PGP key {KeyId} generated for {KeyName}", keyId, keyName);
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

        var signaturePath = artifactPath + ".sig";

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
            var startInfo = new ProcessStartInfo
            {
                FileName = "gpg",
                Arguments = $"--detach-sign --armor --output {signaturePath} {artifactPath}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

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