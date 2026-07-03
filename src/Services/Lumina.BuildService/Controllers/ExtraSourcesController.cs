using Lumina.BuildService.Data;
using Lumina.Shared.DTOs;
using Lumina.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Controllers;

/// <summary>
/// Manages user-uploaded "extra sources" (patches, tarballs, etc.) that are
/// copied into the build container's SOURCES/ on every build of a pipeline.
///
/// Files are isolated per pipeline under
/// <c>/opt/lumina/extra-sources/pipelines/{pipelineId}/</c>, which is the exact
/// directory <see cref="Services.PipelineEngine"/> mounts into builds. All
/// client-supplied path components (the optional subFolder and the uploaded
/// file name) are confined to that per-pipeline root via
/// <see cref="ProcessArgumentSanitizer.ResolveConfinedPath"/>, and the file
/// extension must match the allow-list enforced by the UI.
/// </summary>
[ApiController]
[Route("api/extra-sources")]
public class ExtraSourcesController : ControllerBase
{
    // Host-side root for pipeline-scoped extra sources. Matches the volume
    // mounted in deploy/docker-compose.yml and the directory created in
    // Program.cs, and the path PipelineEngine reads at build time.
    private const string PipelinesRoot = "/opt/lumina/extra-sources/pipelines";

    // Per-file size cap. Kestrel/FormOptions are configured for unlimited
    // uploads (Program.cs), so this is the real enforcement point.
    private readonly long _maxFileSizeBytes;

    // Allowed file extensions — mirrors the InputFile Accept attribute in
    // Pipelines.razor so the server is the source of truth, not the client.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".txz",
        ".zip", ".tar", ".gz", ".bz2", ".xz",
        ".patch", ".diff",
        ".rar", ".7z"
    };

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private readonly BuildDbContext _db;
    private readonly ILogger<ExtraSourcesController> _logger;

    public ExtraSourcesController(BuildDbContext db, IConfiguration config, ILogger<ExtraSourcesController> logger)
    {
        _db = db;
        _logger = logger;
        _maxFileSizeBytes = ParseLongConfig(config, "ExtraSources:MaxFileSizeBytes", 1024L * 1024 * 1024); // 1 GiB default
    }

    private static long ParseLongConfig(IConfiguration config, string key, long defaultValue)
    {
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        return long.TryParse(raw.Trim(), out var v) ? v : defaultValue;
    }

    // GET /api/extra-sources/pipeline/{pipelineId}
    [HttpGet("pipeline/{pipelineId:guid}")]
    public async Task<ActionResult<ApiResponse<List<UploadedSourceResponse>>>> ListPipelineSources(Guid pipelineId)
    {
        if (!await PipelineExistsAsync(pipelineId))
            return NotFound(new ApiResponse<List<UploadedSourceResponse>>(false, null, "Pipeline not found", null));

        var pipelineDir = PipelineDir(pipelineId);
        var result = new List<UploadedSourceResponse>();
        if (!Directory.Exists(pipelineDir))
            return Ok(new ApiResponse<List<UploadedSourceResponse>>(true, result, null, null));

        foreach (var file in Directory.EnumerateFiles(pipelineDir, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            var relativePath = Path.GetRelativePath(pipelineDir, file).Replace('\\', '/');
            result.Add(new UploadedSourceResponse(
                info.Name,
                $"pipelines/{pipelineId}/{relativePath}",
                info.Length,
                info.CreationTimeUtc,
                relativePath.Contains('/') ? relativePath[..relativePath.LastIndexOf('/')] : null));
        }

        // Newest first — matches how the UI prefers to show recently uploaded files
        result.Sort((a, b) => b.UploadedAt.CompareTo(a.UploadedAt));
        return Ok(new ApiResponse<List<UploadedSourceResponse>>(true, result, null, null));
    }

    // POST /api/extra-sources/pipeline/{pipelineId}
    // Multipart fields: "files" (one or more) + optional "subFolder".
    [HttpPost("pipeline/{pipelineId:guid}")]
    [RequestSizeLimit(long.MaxValue)]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<ActionResult<ApiResponse<List<UploadedSourceResponse>>>> UploadPipelineSources(
        Guid pipelineId, [FromForm] string? subFolder, CancellationToken cancellationToken)
    {
        if (!await PipelineExistsAsync(pipelineId))
            return NotFound(new ApiResponse<List<UploadedSourceResponse>>(false, null, "Pipeline not found", null));

        if (Request.Form.Files.Count == 0)
            return BadRequest(new ApiResponse<List<UploadedSourceResponse>>(false, null, "No files uploaded", null));

        // Resolve the trusted per-pipeline root and the optional subFolder once;
        // every file is then confined underneath it.
        var pipelineDir = PipelineDir(pipelineId);
        string subDir;
        try
        {
            subDir = BuildSubFolderPath(pipelineDir, subFolder);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiResponse<List<UploadedSourceResponse>>(false, null, ex.Message, null));
        }

        Directory.CreateDirectory(subDir);

        var uploaded = new List<UploadedSourceResponse>();
        foreach (var file in Request.Form.Files)
        {
            // Size cap — checked before any bytes hit disk.
            if (file.Length > _maxFileSizeBytes)
            {
                return BadRequest(new ApiResponse<List<UploadedSourceResponse>>(false, null,
                    $"File '{file.FileName}' exceeds the maximum allowed size of {_maxFileSizeBytes} bytes", null));
            }

            string safeFileName;
            string targetPath;
            try
            {
                safeFileName = SanitizeFileName(file.FileName);
                if (!IsAllowedExtension(safeFileName))
                    return BadRequest(new ApiResponse<List<UploadedSourceResponse>>(false, null,
                        $"File type '{file.FileName}' is not allowed. Permitted: source archives and patches only.", null));

                // Final choke point: confine the combined path to the per-pipeline root.
                targetPath = ProcessArgumentSanitizer.ResolveConfinedPath(Path.Combine(subDir, safeFileName), pipelineDir);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ApiResponse<List<UploadedSourceResponse>>(false, null, ex.Message, null));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            await using (var src = file.OpenReadStream())
            await using (var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await src.CopyToAsync(dst, cancellationToken);
            }

            var info = new FileInfo(targetPath);
            var relativePath = Path.GetRelativePath(pipelineDir, targetPath).Replace('\\', '/');
            uploaded.Add(new UploadedSourceResponse(
                info.Name,
                $"pipelines/{pipelineId}/{relativePath}",
                info.Length,
                info.CreationTimeUtc,
                relativePath.Contains('/') ? relativePath[..relativePath.LastIndexOf('/')] : null));

            _logger.LogInformation("Stored extra source {File} for pipeline {PipelineId} ({Size} bytes)",
                safeFileName, pipelineId, info.Length);
        }

        return Ok(new ApiResponse<List<UploadedSourceResponse>>(true, uploaded, null, $"Uploaded {uploaded.Count} file(s)"));
    }

    // DELETE /api/extra-sources/pipeline/{pipelineId}/{filePath}
    [HttpDelete("pipeline/{pipelineId:guid}/{*filePath}")]
    public async Task<ActionResult<ApiResponse<object>>> DeletePipelineSource(Guid pipelineId, string filePath)
    {
        if (!await PipelineExistsAsync(pipelineId))
            return NotFound(new ApiResponse<object>(false, null, "Pipeline not found", null));

        var pipelineDir = PipelineDir(pipelineId);
        string target;
        try
        {
            // Confine the client-supplied relative path to the per-pipeline root.
            target = ProcessArgumentSanitizer.ResolveConfinedPath(filePath, pipelineDir);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiResponse<object>(false, null, ex.Message, null));
        }

        if (!System.IO.File.Exists(target))
            return NotFound(new ApiResponse<object>(false, null, "File not found", null));

        try
        {
            System.IO.File.Delete(target);
            CleanupEmptySubfolders(pipelineDir, target);
            _logger.LogInformation("Deleted extra source {Path} for pipeline {PipelineId}", filePath, pipelineId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete extra source {Path} for pipeline {PipelineId}", filePath, pipelineId);
            return StatusCode(500, new ApiResponse<object>(false, null, "Failed to delete file", null));
        }

        return Ok(new ApiResponse<object>(true, null, null, "File deleted"));
    }

    // DELETE /api/extra-sources/pipeline/{pipelineId}
    [HttpDelete("pipeline/{pipelineId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> ClearPipelineSources(Guid pipelineId)
    {
        if (!await PipelineExistsAsync(pipelineId))
            return NotFound(new ApiResponse<object>(false, null, "Pipeline not found", null));

        var pipelineDir = PipelineDir(pipelineId);
        if (Directory.Exists(pipelineDir))
        {
            try
            {
                Directory.Delete(pipelineDir, recursive: true);
                _logger.LogInformation("Cleared all extra sources for pipeline {PipelineId}", pipelineId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear extra sources for pipeline {PipelineId}", pipelineId);
                return StatusCode(500, new ApiResponse<object>(false, null, "Failed to clear sources", null));
            }
        }

        return Ok(new ApiResponse<object>(true, null, null, "All extra sources cleared"));
    }

    // --- helpers ---

    private static string PipelineDir(Guid pipelineId) => Path.Combine(PipelinesRoot, pipelineId.ToString());

    private async Task<bool> PipelineExistsAsync(Guid pipelineId)
        => await _db.Pipelines.AnyAsync(p => p.Id == pipelineId);

    /// <summary>
    /// Validates the optional subFolder and returns the absolute subdirectory
    /// under <paramref name="pipelineDir"/>. Empty/null means the pipeline root
    /// itself. Traversal, absolute paths, and invalid path characters are
    /// rejected; only simple nested directory names are allowed.
    /// </summary>
    private static string BuildSubFolderPath(string pipelineDir, string? subFolder)
    {
        if (string.IsNullOrWhiteSpace(subFolder))
            return pipelineDir;

        // Normalize separators and strip leading/trailing slashes; reject any
        // attempt to escape the per-pipeline root.
        var normalized = subFolder.Trim().Trim('/', '\\').Replace('\\', '/');

        // Re-check after trimming — input like "///" normalizes to empty.
        if (string.IsNullOrWhiteSpace(normalized))
            return pipelineDir;

        if (normalized.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Subfolder must not contain parent-directory references", nameof(subFolder));

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var combined = pipelineDir;
        foreach (var segment in segments)
        {
            if (segment.Equals(".", StringComparison.Ordinal) || segment.Equals("..", StringComparison.Ordinal))
                throw new ArgumentException("Subfolder contains an invalid path segment", nameof(subFolder));
            if (segment.IndexOfAny(InvalidFileNameChars) >= 0)
                throw new ArgumentException("Subfolder contains invalid characters", nameof(subFolder));

            combined = Path.Combine(combined, segment);
        }

        // Final canonical confinement — catches anything the segment check missed.
        return ProcessArgumentSanitizer.ResolveConfinedPath(combined, pipelineDir);
    }

    /// <summary>
    /// Reduces a client-supplied file name to a bare, safe file name (no path
    /// components, no invalid characters, no traversal).
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required", nameof(fileName));

        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name) || name.Equals(".", StringComparison.Ordinal) || name.Equals("..", StringComparison.Ordinal))
            throw new ArgumentException($"Invalid file name: {fileName}", nameof(fileName));

        if (name.AsSpan().IndexOfAny(InvalidFileNameChars) >= 0)
            throw new ArgumentException($"File name contains invalid characters: {fileName}", nameof(fileName));

        return name;
    }

    private static bool IsAllowedExtension(string fileName)
    {
        return AllowedExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Removes now-empty intermediate subdirectories left after a file delete,
    /// stopping at <paramref name="root"/> (which is preserved).
    /// </summary>
    private static void CleanupEmptySubfolders(string root, string deletedFile)
    {
        var dir = Path.GetDirectoryName(deletedFile);
        var rootFull = Path.GetFullPath(root);
        while (!string.IsNullOrEmpty(dir) && dir.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && dir != rootFull)
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                    break;
                Directory.Delete(dir);
            }
            catch
            {
                break; // best-effort cleanup
            }
            dir = Path.GetDirectoryName(dir);
        }
    }
}
