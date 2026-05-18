using System.Security.Cryptography;
using Lumina.SecurityService.Data;
using Lumina.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SecurityService.Services;

public class HashService
{
    private readonly SecurityDbContext _db;
    private readonly ILogger<HashService> _logger;

    public HashService(SecurityDbContext db, ILogger<HashService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<HashRecord> ComputeHashesAsync(Guid artifactId, string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var fileInfo = new FileInfo(filePath);

        string sha256, md5, sha1;

        await using (var stream = File.OpenRead(filePath))
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            stream.Position = 0;
            md5 = Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
            stream.Position = 0;
            sha1 = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
        }

        var record = new HashRecord
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifactId,
            FileName = fileInfo.Name,
            Sha256 = sha256,
            Md5 = md5,
            Sha1 = sha1,
            FileSize = fileInfo.Length,
            CreatedAt = DateTime.UtcNow
        };

        _db.HashRecords.Add(record);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Hash computed for {FileName}: SHA256={Sha256}", fileInfo.Name, sha256[..16] + "...");
        return record;
    }

    public async Task<HashRecord?> VerifyHashAsync(Guid artifactId, string filePath)
    {
        var existing = await _db.HashRecords.FirstOrDefaultAsync(h => h.ArtifactId == artifactId);
        if (existing == null) return null;

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        await using var stream = File.OpenRead(filePath);
        var currentSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();

        if (currentSha256 != existing.Sha256)
        {
            _logger.LogWarning("Hash mismatch for {FileName}: expected {Expected}, got {Actual}",
                existing.FileName, existing.Sha256[..16], currentSha256[..16]);
        }

        return existing;
    }

    public async Task<List<HashRecord>> GetHashHistoryAsync(Guid artifactId)
    {
        return await _db.HashRecords
            .Where(h => h.ArtifactId == artifactId)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Store pre-computed hashes from BuildService (which has the actual files).
    /// </summary>
    public async Task<HashRecord> StorePrecomputedHashAsync(Guid artifactId, string fileName, string sha256, string md5, long fileSize)
    {
        // Check if hash already exists for this artifact
        var existing = await _db.HashRecords.FirstOrDefaultAsync(h => h.ArtifactId == artifactId);
        if (existing != null)
        {
            _logger.LogInformation("Hash already exists for artifact {ArtifactId}, updating", artifactId);
            existing.Sha256 = sha256;
            existing.Md5 = md5;
            existing.FileName = fileName;
            existing.FileSize = fileSize;
            existing.CreatedAt = DateTime.UtcNow;
            _db.HashRecords.Update(existing);
            await _db.SaveChangesAsync();
            return existing;
        }

        var record = new HashRecord
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifactId,
            FileName = fileName,
            Sha256 = sha256,
            Md5 = md5,
            Sha1 = "",
            FileSize = fileSize,
            CreatedAt = DateTime.UtcNow
        };

        _db.HashRecords.Add(record);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Stored pre-computed hash for {FileName}: SHA256={Sha256}", fileName, sha256[..Math.Min(16, sha256.Length)] + "...");
        return record;
    }

    /// <summary>
    /// Get all hash records, newest first.
    /// </summary>
    public async Task<List<HashRecord>> GetAllHashesAsync(int page = 1, int pageSize = 20)
    {
        return await _db.HashRecords
            .OrderByDescending(h => h.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<int> GetTotalHashCountAsync()
    {
        return await _db.HashRecords.CountAsync();
    }

}
