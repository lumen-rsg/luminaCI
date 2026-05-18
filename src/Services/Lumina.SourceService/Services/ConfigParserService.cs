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

    /// <summary>
    /// Save raw conf.ini content (full replace)
    /// </summary>
    public void SaveContent(string content)
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_configPath, content);
        _cachedPackages = null; // Invalidate cache
        _lastReadTime = DateTime.MinValue;
        _logger.LogInformation("Saved config to {Path}", _configPath);
    }

    /// <summary>
    /// Add a new package entry to conf.ini
    /// </summary>
    public void AddPackage(PackageSourceConfig package)
    {
        var sb = new System.Text.StringBuilder();

        // Read existing content
        if (File.Exists(_configPath))
            sb.Append(File.ReadAllText(_configPath));

        // Ensure trailing newline
        if (sb.Length > 0 && !sb.ToString().EndsWith('\n'))
            sb.AppendLine();

        sb.AppendLine("[package]");
        sb.AppendLine($"name=\"{package.Name}\"");
        sb.AppendLine($"source=\"{package.Source}\"");
        sb.AppendLine($"source_type=\"{FormatSourceType(package.SourceType)}\"");
        if (!string.IsNullOrEmpty(package.SourceBranch))
            sb.AppendLine($"source_branch=\"{package.SourceBranch}\"");
        if (!string.IsNullOrEmpty(package.BuildImage))
            sb.AppendLine($"build_image=\"{package.BuildImage}\"");

        SaveContent(sb.ToString());
        _logger.LogInformation("Added package {Name} to config", package.Name);
    }

    /// <summary>
    /// Remove a package from conf.ini by name
    /// </summary>
    public bool RemovePackage(string name)
    {
        if (!File.Exists(_configPath)) return false;

        var content = File.ReadAllText(_configPath);
        var lines = content.Split('\n').ToList();
        var newLines = new List<string>();
        bool inTargetPackage = false;
        bool removed = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();

            if (line.Equals("[package]", StringComparison.OrdinalIgnoreCase))
            {
                // Check if this section's name matches
                inTargetPackage = false;
                // Look ahead for the name
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var ahead = lines[j].Trim();
                    if (ahead.StartsWith('[')) break;
                    if (ahead.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = ahead.Split('=', 2).LastOrDefault()?.Trim().Trim('"') ?? "";
                        if (string.Equals(val, name, StringComparison.OrdinalIgnoreCase))
                        {
                            inTargetPackage = true;
                            removed = true;
                        }
                        break;
                    }
                }

                if (!inTargetPackage)
                    newLines.Add(lines[i]);
                continue;
            }

            if (inTargetPackage)
            {
                // Skip lines until next section or empty-ish
                if (line.StartsWith('['))
                {
                    inTargetPackage = false;
                    newLines.Add(lines[i]);
                }
                // Skip this line (part of removed package)
            }
            else
            {
                newLines.Add(lines[i]);
            }
        }

        if (removed)
        {
            SaveContent(string.Join('\n', newLines));
            _logger.LogInformation("Removed package {Name} from config", name);
        }
        return removed;
    }

    private static string FormatSourceType(SourceType type) => type switch
    {
        SourceType.Git => "git",
        SourceType.Tar => "tar",
        SourceType.Http => "http",
        SourceType.Ftp => "ftp",
        SourceType.Rsync => "rsync",
        SourceType.Svn => "svn",
        SourceType.Hg => "hg",
        SourceType.Local => "local",
        _ => "http"
    };

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