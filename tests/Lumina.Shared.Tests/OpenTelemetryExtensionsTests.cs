using Lumina.Web.Shared.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Lumina.Shared.Tests;

public sealed class OpenTelemetryExtensionsTests
{
    [Fact]
    public void LeavesServicesUnchangedWhenExporterIsNotConfigured()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var result = services.AddLuminaOpenTelemetry(
            configuration, "lumina-test-service");

        Assert.Same(services, result);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("collector:4317")]
    [InlineData("ftp://collector:4317")]
    [InlineData("http://user:password@collector:4317")]
    public void RejectsUnsafeOrAmbiguousExporterEndpoint(string endpoint)
    {
        var services = new ServiceCollection();
        var configuration = Configuration(endpoint);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddLuminaOpenTelemetry(configuration, "lumina-test-service"));

        Assert.Contains(
            OpenTelemetryExtensions.OtlpEndpointEnvironmentVariable,
            exception.Message);
    }

    [Fact]
    public void RegistersTelemetryHostedServiceForValidExporterEndpoint()
    {
        var services = new ServiceCollection();

        services.AddLuminaOpenTelemetry(
            Configuration("http://otel-collector:4317"),
            "lumina-test-service");

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService));
    }

    private static IConfiguration Configuration(string endpoint)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OpenTelemetryExtensions.OtlpEndpointEnvironmentVariable] = endpoint
            })
            .Build();
}
