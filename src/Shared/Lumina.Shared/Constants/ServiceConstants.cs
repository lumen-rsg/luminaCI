namespace Lumina.Shared.Constants;

public static class ServiceConstants
{
    public static class Ports
    {
        public const int ApiGateway = 5000;
        public const int BuildService = 5001;
        public const int SecurityService = 5002;
        public const int ScannerService = 5003;
        public const int RepositoryService = 5004;
        public const int WebApp = 5005;
    }

    public static class QueueNames
    {
        public const string BuildQueue = "lumina-build-queue";
        public const string SecurityQueue = "lumina-security-queue";
        public const string ScannerQueue = "lumina-scanner-queue";
        public const string RepositoryQueue = "lumina-repository-queue";
    }

    public static class StorageBuckets
    {
        public const string Artifacts = "lumina-artifacts";
        public const string Repositories = "lumina-repositories";
        public const string Logs = "lumina-logs";
    }

    public static class Domains
    {
        public const string Console = "console.lumina.1t.ru";
        public const string Packages = "packages.lumina.1t.ru";
    }
}