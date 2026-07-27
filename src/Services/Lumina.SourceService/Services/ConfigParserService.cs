using Lumina.Shared.Models.Enums;

namespace Lumina.SourceService.Services;

/// <summary>
/// Immutable package entry imported from the legacy conf.ini manifest.
/// </summary>
public sealed record PackageSourceConfig(
    string Name,
    string Source,
    SourceType SourceType,
    string? SourceBranch,
    string? BuildImage,
    string? SpecPath);

/// <summary>
/// Raised when the legacy source manifest is missing or invalid. The parser is
/// deliberately fail-closed because its output determines what code is fetched
/// and built.
/// </summary>
public sealed class ConfigParseException : Exception
{
    public ConfigParseException(string message) : base(message) { }
    public ConfigParseException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Strict, read-only importer for legacy conf.ini package definitions.
/// </summary>
public sealed class ConfigParserService
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "source",
        "source_type",
        "source_branch",
        "build_image",
        "spec_path"
    };

    private readonly ILogger<ConfigParserService> _logger;
    private readonly string _configPath;

    public ConfigParserService(ILogger<ConfigParserService> logger, IConfiguration config)
    {
        _logger = logger;
        _configPath = config["Source:ConfigPath"] ?? "/app/conf.ini";
    }

    /// <summary>
    /// Parse a fresh immutable snapshot. No stale cache is returned after a
    /// missing-file, read, or validation failure.
    /// </summary>
    public IReadOnlyList<PackageSourceConfig> ParsePackages()
    {
        if (!File.Exists(_configPath))
            throw new ConfigParseException($"Source configuration file is missing: {_configPath}");

        try
        {
            var packages = ParseIniContent(File.ReadAllText(_configPath));
            _logger.LogInformation("Parsed {Count} packages from {Path}", packages.Count, _configPath);
            return packages;
        }
        catch (ConfigParseException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read source configuration: {Path}", _configPath);
            throw new ConfigParseException($"Source configuration could not be read: {_configPath}", ex);
        }
    }

    public PackageSourceConfig? GetPackage(string name)
        => ParsePackages().FirstOrDefault(package =>
            string.Equals(package.Name, name, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<PackageSourceConfig> ParseIniContent(string content)
    {
        var packages = new List<PackageSourceConfig>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? section = null;
        var sectionLine = 0;
        var lines = content.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('['))
            {
                if (!line.Equals("[package]", StringComparison.OrdinalIgnoreCase))
                    throw Error(lineNumber, $"unknown section '{line}'");

                if (section != null)
                    AddPackage(section, sectionLine, packages, names);

                section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sectionLine = lineNumber;
                continue;
            }

            if (section == null)
                throw Error(lineNumber, "property appears outside a [package] section");

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
                throw Error(lineNumber, "expected key=value");

            var key = line[..equalsIndex].Trim();
            if (!AllowedKeys.Contains(key))
                throw Error(lineNumber, $"unknown property '{key}'");
            if (section.ContainsKey(key))
                throw Error(lineNumber, $"duplicate property '{key}'");

            section[key] = ParseValue(line[(equalsIndex + 1)..], lineNumber);
        }

        if (section != null)
            AddPackage(section, sectionLine, packages, names);

        if (packages.Count == 0)
            throw new ConfigParseException("Source configuration contains no [package] sections");

        return Array.AsReadOnly(packages.ToArray());
    }

    private static void AddPackage(
        IReadOnlyDictionary<string, string> values,
        int sectionLine,
        ICollection<PackageSourceConfig> packages,
        ISet<string> names)
    {
        var name = Required(values, "name", sectionLine);
        var source = Required(values, "source", sectionLine);
        var sourceType = ParseSourceType(Required(values, "source_type", sectionLine), sectionLine);

        if (name.Length > 100 ||
            name is "." or ".." ||
            name.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw Error(sectionLine, "package name must be a safe slug of at most 100 characters");
        }
        if (!names.Add(name))
            throw Error(sectionLine, $"duplicate package name '{name}'");

        ValidateSource(source, sourceType, sectionLine);

        var specPath = Optional(values, "spec_path", sectionLine);
        if (specPath != null &&
            (Path.IsPathRooted(specPath) ||
             specPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..")))
        {
            throw Error(sectionLine, "spec_path must be a confined relative path");
        }

        packages.Add(new PackageSourceConfig(
            name,
            source,
            sourceType,
            Optional(values, "source_branch", sectionLine),
            Optional(values, "build_image", sectionLine),
            specPath));
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string key,
        int sectionLine)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw Error(sectionLine, $"required property '{key}' is missing or empty");
        return value;
    }

    private static string? Optional(
        IReadOnlyDictionary<string, string> values,
        string key,
        int sectionLine)
    {
        if (!values.TryGetValue(key, out var value))
            return null;
        if (string.IsNullOrWhiteSpace(value))
            throw Error(sectionLine, $"optional property '{key}' must not be empty when present");
        return value;
    }

    private static string ParseValue(string rawValue, int lineNumber)
    {
        var value = rawValue.Trim();
        if (value.Length == 0)
            return string.Empty;

        var startsQuoted = value.StartsWith('"');
        var endsQuoted = value.EndsWith('"');
        if (startsQuoted != endsQuoted || (startsQuoted && value.Length < 2))
            throw Error(lineNumber, "quoted value is not terminated");

        if (startsQuoted)
            value = value[1..^1];

        if (value.Contains('"') || value.Any(character => character is '\0' or '\r' or '\n'))
            throw Error(lineNumber, "value contains invalid characters");

        return value;
    }

    private static SourceType ParseSourceType(string value, int lineNumber) => value.ToLowerInvariant() switch
    {
        "git" => SourceType.Git,
        "tar" or "targz" or "tar.gz" or "tar.bz2" or "tar.xz" => SourceType.Tar,
        "http" or "https" => SourceType.Http,
        "ftp" => SourceType.Ftp,
        "rsync" => SourceType.Rsync,
        "svn" or "subversion" => SourceType.Svn,
        "hg" or "mercurial" => SourceType.Hg,
        "local" => SourceType.Local,
        _ => throw Error(lineNumber, $"unknown source_type '{value}'")
    };

    private static void ValidateSource(string source, SourceType sourceType, int lineNumber)
    {
        if (sourceType == SourceType.Local)
        {
            if (Path.IsPathRooted(source) || source.Split(['/', '\\']).Contains(".."))
                throw Error(lineNumber, "local source must be a confined relative path");
            return;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            throw Error(lineNumber, "source must be an absolute URI");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw Error(lineNumber, "source URI must not contain embedded credentials");

        var scheme = uri.Scheme.ToLowerInvariant();
        var allowed = sourceType switch
        {
            SourceType.Git => scheme is "http" or "https" or "git" or "ssh",
            SourceType.Tar or SourceType.Http => scheme is "http" or "https",
            SourceType.Ftp => scheme == "ftp",
            SourceType.Rsync => scheme == "rsync",
            SourceType.Svn => scheme is "http" or "https" or "svn" or "svn+ssh",
            SourceType.Hg => scheme is "http" or "https" or "ssh",
            _ => false
        };

        if (!allowed)
            throw Error(lineNumber, $"source URI scheme '{scheme}' does not match source_type");
    }

    private static ConfigParseException Error(int lineNumber, string message)
        => new($"Invalid source configuration at line {lineNumber}: {message}");
}
