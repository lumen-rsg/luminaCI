using Lumina.Shared.Models.Enums;

namespace Lumina.SourceService.Services;

/// <summary>
/// Represents a parsed package entry from conf.ini
/// </summary>
public class PackageSourceConfig
{
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public SourceType SourceType { get; set; }
    public string? SourceBranch { get; set; }
    public string? BuildImage { get; set; }
    public string? SpecContent { get; set; }
}

/// <summary>
/// Parses conf.ini files with [package] sections containing name, source, source_type, source_branch
/// </summary>
public class ConfigParserService
{
    private readonly ILogger<ConfigParserService> _logger;
    private readonly IConfiguration _config;
    private List<PackageSourceConfig>? _cachedPackages;
    private DateTime _lastReadTime = DateTime.MinValue;
    private readonly string _configPath;

    public ConfigParserService(ILogger<ConfigParserService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
        _configPath = config["Source:ConfigPath"] ?? "/app/conf.ini";
    }

    /// <summary>
    /// Parse all package entries from conf.ini. Results are cached and refreshed
    /// when the file modification time changes.
    /// </summary>
    public List<PackageSourceConfig> ParsePackages()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                _logger.LogWarning("Config file not found: {Path}", _configPath);
                return [];
            }

            var lastWrite = File.GetLastWriteTimeUtc(_configPath);
            if (_cachedPackages != null && lastWrite <= _lastReadTime)
            {
                return _cachedPackages;
            }

            var content = File.ReadAllText(_configPath);
            var packages = ParseIniContent(content);

            _cachedPackages = packages;
            _lastReadTime = lastWrite;

            _logger.LogInformation("Parsed {Count} packages from {Path}", packages.Count, _configPath);
            return packages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse config file: {Path}", _configPath);
            return _cachedPackages ?? [];
        }
    }

    /// <summary>
    /// Get a specific package by name
    /// </summary>
    public PackageSourceConfig? GetPackage(string name)
    {
        return ParsePackages().FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static List<PackageSourceConfig> ParseIniContent(string content)
    {
        var packages = new List<PackageSourceConfig>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        PackageSourceConfig? current = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // New [package] section
            if (line.Equals("[package]", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null && !string.IsNullOrWhiteSpace(current.Name))
                {
                    packages.Add(current);
                }
                current = new PackageSourceConfig();
                continue;
            }

            if (current == null) continue;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith(';')) continue;

            var eqIndex = line.IndexOf('=');
            if (eqIndex < 0) continue;

            var key = line[..eqIndex].Trim().ToLowerInvariant();
            var value = line[(eqIndex + 1)..].Trim().Trim('"');

            switch (key)
            {
                case "name":
                    current.Name = value;
                    break;
                case "source":
                    current.Source = value;
                    break;
                case "sources":
                    // Typo in conf.ini — treat same as "source"
                    current.Source = value;
                    break;
                case "source_type":
                    current.SourceType = ParseSourceType(value);
                    break;
                case "source_branch":
                    current.SourceBranch = value;
                    break;
                case "build_image":
                    current.BuildImage = value;
                    break;
            }
        }

        // Don't forget the last package
        if (current != null && !string.IsNullOrWhiteSpace(current.Name))
        {
            packages.Add(current);
        }

        return packages;
    }

    /// <summary>
    /// Get the raw conf.ini content
    /// </summary>
    public string GetRawContent()
    {
        if (!File.Exists(_configPath))
            return string.Empty;
        return File.ReadAllText(_configPath);
    }

    private static SourceType ParseSourceType(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "git" => SourceType.Git,
            "tar" or "targz" or "tar.gz" or "tar.bz2" or "tar.xz" => SourceType.Tar,
            "http" or "https" => SourceType.Http,
            "ftp" => SourceType.Ftp,
            "rsync" => SourceType.Rsync,
            "svn" or "subversion" => SourceType.Svn,
            "hg" or "mercurial" => SourceType.Hg,
            "local" => SourceType.Local,
            _ => SourceType.Http // Default to HTTP for unknown types
        };
    }
}
