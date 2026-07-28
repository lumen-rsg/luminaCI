namespace Lumina.McpServer;

public sealed record LuminaOptions(
    Uri BaseUri,
    string Username,
    string Password,
    bool AllowInvalidTls)
{
    public static LuminaOptions FromEnvironment()
    {
        var rawUrl = Environment.GetEnvironmentVariable("LUMINA_URL")
            ?? "https://localhost";
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp &&
             baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "LUMINA_URL must be an absolute HTTP or HTTPS URL.");
        }

        var username = Required("LUMINA_USERNAME");
        var password = Required("LUMINA_PASSWORD");
        var allowInvalidTls = bool.TryParse(
            Environment.GetEnvironmentVariable("LUMINA_INSECURE_TLS"),
            out var insecure) && insecure;

        return new LuminaOptions(baseUri, username, password, allowInvalidTls);
    }

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is required.");
        return value;
    }
}
