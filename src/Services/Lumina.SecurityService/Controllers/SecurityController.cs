using Lumina.Shared.DTOs;
using Lumina.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace Lumina.SecurityService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SecurityController : ControllerBase
{
    private readonly Services.PgpSigningService _pgp;
    private readonly Services.HashService _hash;
    private readonly ILogger<SecurityController> _logger;

    public SecurityController(Services.PgpSigningService pgp, Services.HashService hash, ILogger<SecurityController> logger)
    {
        _pgp = pgp;
        _hash = hash;
        _logger = logger;
    }

    // === Key Management ===

    [HttpGet("keys")]
    public async Task<ActionResult<ApiResponse<List<SecurityKey>>>> ListKeys()
    {
        var keys = await _pgp.ListKeysAsync();
        return Ok(new ApiResponse<List<SecurityKey>>(true, keys, null, null));
    }

    [HttpPost("keys/generate")]
    public async Task<ActionResult<ApiResponse<SecurityKey>>> GenerateKey([FromBody] GenerateKeyRequest request)
    {
        var key = await _pgp.GenerateKeyAsync(request.KeyName, request.Email, request.Passphrase, "system");
        return CreatedAtAction(nameof(ListKeys), new ApiResponse<SecurityKey>(true, key, null, "Key generated"));
    }

    // === Signing ===

    [HttpPost("sign")]
    public async Task<ActionResult<ApiResponse<SigningRequest>>> SignArtifact([FromBody] SignArtifactRequest request)
    {
        try
        {
            var result = await _pgp.SignArtifactAsync(request.ArtifactId, request.ArtifactPath, request.KeyId);
            return Ok(new ApiResponse<SigningRequest>(true, result, null, "Artifact signed"));
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<SigningRequest>(false, null, ex.Message, null));
        }
    }

    [HttpGet("signing/history")]
    public async Task<ActionResult<ApiResponse<List<SigningRequest>>>> SigningHistory([FromQuery] int count = 50)
    {
        var requests = await _pgp.GetRecentSigningsAsync(count);
        return Ok(new ApiResponse<List<SigningRequest>>(true, requests, null, null));
    }

    // === Hash ===

    [HttpPost("hash/compute")]
    public async Task<ActionResult<ApiResponse<HashRecord>>> ComputeHash([FromBody] ComputeHashRequest request)
    {
        try
        {
            var record = await _hash.ComputeHashesAsync(request.ArtifactId, request.FilePath);
            return Ok(new ApiResponse<HashRecord>(true, record, null, "Hash computed"));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new ApiResponse<HashRecord>(false, null, ex.Message, null));
        }
    }

    [HttpPost("hash/verify")]
    public async Task<ActionResult<ApiResponse<HashRecord>>> VerifyHash([FromBody] ComputeHashRequest request)
    {
        try
        {
            var record = await _hash.VerifyHashAsync(request.ArtifactId, request.FilePath);
            if (record == null) return NotFound(new ApiResponse<HashRecord>(false, null, "No hash record found", null));
            return Ok(new ApiResponse<HashRecord>(true, record, null, "Hash verified"));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new ApiResponse<HashRecord>(false, null, ex.Message, null));
        }
    }

    [HttpGet("hash/{artifactId:guid}")]
    public async Task<ActionResult<ApiResponse<List<HashRecord>>>> HashHistory(Guid artifactId)
    {
        var records = await _hash.GetHashHistoryAsync(artifactId);
        return Ok(new ApiResponse<List<HashRecord>>(true, records, null, null));
    }
}