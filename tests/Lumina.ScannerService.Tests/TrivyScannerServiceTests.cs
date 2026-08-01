using System.Diagnostics;
using Lumina.ScannerService.Services;
using Xunit;

namespace Lumina.ScannerService.Tests;

public class TrivyScannerServiceTests
{
    [Fact]
    public void RpmExtraction_AllowsForeignArchitectureWithoutRunningScripts()
    {
        var arguments = TrivyScannerService.BuildRpmInstallArguments(
            "/tmp/lumina-rootfs/test",
            "/tmp/kernel-tegra-l4t.aarch64.rpm");

        Assert.Contains("--ignorearch", arguments);
        Assert.Contains("--nodeps", arguments);
        Assert.Contains("--noscripts", arguments);
        Assert.Contains("--notriggers", arguments);
        Assert.Equal("/tmp/kernel-tegra-l4t.aarch64.rpm", arguments[^1]);
    }

    [Fact]
    public async Task WaitForExitOrKillAsync_TerminatesHungProcess()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { "-c", "sleep 30 & wait" },
            UseShellExecute = false
        })!;

        var exitedNormally = await TrivyScannerService.WaitForExitOrKillAsync(
            process, TimeSpan.FromMilliseconds(100));

        Assert.False(exitedNormally);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task WaitForExitOrKillAsync_ReturnsTrueForCompletedProcess()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { "-c", "exit 0" },
            UseShellExecute = false
        })!;

        var exitedNormally = await TrivyScannerService.WaitForExitOrKillAsync(
            process, TimeSpan.FromSeconds(5));

        Assert.True(exitedNormally);
        Assert.Equal(0, process.ExitCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"error":"scanner unavailable"}""")]
    [InlineData("""{"Results":null}""")]
    [InlineData("""{"Results":{}}""")]
    public void ParseCliResponse_RejectsUnrecognizedSchemas(string json)
    {
        var result = TrivyScannerService.ParseTrivyCliResponse(json);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Vulnerabilities);
    }

    [Theory]
    [InlineData("""{"Results":[]}""")]
    [InlineData("""{"Results":[{"Vulnerabilities":null}]}""")]
    [InlineData("""{"Results":[{}]}""")]
    public void ParseCliResponse_AcceptsRecognizedCleanSchemas(string json)
    {
        var result = TrivyScannerService.ParseTrivyCliResponse(json);

        Assert.True(result.Success);
        Assert.Empty(result.Vulnerabilities);
    }

    [Fact]
    public void ParseCliResponse_AcceptsCompleteServerBackedReportWithoutTargets()
    {
        const string json = """
            {
              "SchemaVersion": 2,
              "Trivy": {
                "Version": "0.72.0",
                "Server": {
                  "Version": "0.72.0",
                  "VulnerabilityDB": {
                    "UpdatedAt": "2026-07-27T19:20:18Z"
                  }
                }
              },
              "CreatedAt": "2026-07-27T22:57:07Z",
              "ArtifactName": "/tmp/lumina-rootfs",
              "ArtifactType": "filesystem",
              "Metadata": {
                "OS": {
                  "Family": "none",
                  "Name": ""
                }
              }
            }
            """;

        var result = TrivyScannerService.ParseTrivyCliResponse(json);

        Assert.True(result.Success);
        Assert.Empty(result.Vulnerabilities);
    }

    [Theory]
    [InlineData("""{"SchemaVersion":2,"ArtifactType":"filesystem"}""")]
    [InlineData("""
        {
          "SchemaVersion": 2,
          "Trivy": {
            "Version": "0.72.0",
            "Server": {
              "Version": "0.72.0",
              "VulnerabilityDB": {}
            }
          },
          "CreatedAt": "2026-07-27T22:57:07Z",
          "ArtifactType": "filesystem",
          "Metadata": {"OS": {}}
        }
        """)]
    public void ParseCliResponse_RejectsIncompleteEmptyReportEnvelope(string json)
    {
        var result = TrivyScannerService.ParseTrivyCliResponse(json);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ParseCliResponse_ParsesVulnerabilities()
    {
        const string json = """
            {
              "Results": [{
                "Vulnerabilities": [{
                  "VulnerabilityID": "CVE-2026-1234",
                  "PkgName": "example",
                  "Severity": "HIGH"
                }]
              }]
            }
            """;

        var result = TrivyScannerService.ParseTrivyCliResponse(json);

        var vulnerability = Assert.Single(result.Vulnerabilities);
        Assert.True(result.Success);
        Assert.Equal("CVE-2026-1234", vulnerability.CveId);
        Assert.Equal("HIGH", vulnerability.Severity);
    }

    [Theory]
    [InlineData("""{"Results":[{"Vulnerabilities":{}}]}""")]
    [InlineData("""{"Results":[{"Vulnerabilities":[null]}]}""")]
    [InlineData("""{"Results":[null]}""")]
    public void ParseCliResponse_RejectsMalformedNestedValues(string json)
    {
        var result = TrivyScannerService.ParseTrivyCliResponse(json);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"error":"scanner unavailable"}""")]
    [InlineData("""{"results":null}""")]
    [InlineData("""{"results":{}}""")]
    public void ParseServerResponse_RejectsUnrecognizedSchemas(string json)
    {
        var result = TrivyScannerService.ParseTrivyServerResponse(json);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Vulnerabilities);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"results":[]}""")]
    [InlineData("""{"results":[{"vulnerabilities":null}]}""")]
    [InlineData("""{"results":[{}]}""")]
    public void ParseServerResponse_AcceptsRecognizedCleanSchemas(string json)
    {
        var result = TrivyScannerService.ParseTrivyServerResponse(json);

        Assert.True(result.Success);
        Assert.Empty(result.Vulnerabilities);
    }

    [Fact]
    public void ParseServerResponse_ParsesVulnerabilities()
    {
        const string json = """
            {
              "results": [{
                "vulnerabilities": [{
                  "vulnerability_id": "CVE-2026-5678",
                  "pkg_name": "example",
                  "severity": "critical"
                }]
              }]
            }
            """;

        var result = TrivyScannerService.ParseTrivyServerResponse(json);

        var vulnerability = Assert.Single(result.Vulnerabilities);
        Assert.True(result.Success);
        Assert.Equal("CVE-2026-5678", vulnerability.CveId);
        Assert.Equal("CRITICAL", vulnerability.Severity);
    }

    [Fact]
    public void ParseResponse_PreservesRawOutputOnFailure()
    {
        const string json = """{"message":"unexpected response"}""";

        var result = TrivyScannerService.ParseTrivyCliResponse(json);

        Assert.False(result.Success);
        Assert.Equal(json, result.RawOutput);
    }
}
