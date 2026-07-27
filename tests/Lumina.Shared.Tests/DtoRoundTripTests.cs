using System.Text.Json;
using Lumina.Shared.DTOs;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Xunit;

namespace Lumina.Shared.Tests;

/// <summary>
/// Round-trip tests for every public request/response DTO in
/// <see cref="Lumina.Shared.DTOs"/>. The <see cref="RedisCacheService"/>
/// contract is "DTOs only" — a value that <c>System.Text.Json</c> cannot
/// reproduce is rejected at write time (see
/// <c>RedisCacheServiceSerializationTests</c>). That guard only helps if the
/// DTOs themselves are round-trippable to begin with; these tests pin that
/// invariant per-type so a future field (a non-serializable struct, a
/// read-only collection without a setter, an init-only property a deserializer
/// can't populate) is caught at the DTO definition, not at the first cache hit
/// in production.
/// </summary>
public class DtoRoundTripTests
{
    // The same options the cache service uses by default. The DTOs must round-
    // trip under the *production* serializer configuration, not a permissive
    // test-only one.
    private static readonly JsonSerializerOptions Options = new();

    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return JsonSerializer.Deserialize<T>(json, Options)!;
    }

    // ─── Request DTOs ────────────────────────────────────────────────────

    [Fact]
    public void CreatePipelineRequest_RoundTrips()
    {
        var req = new CreatePipelineRequest(
            Name: "my-pipeline",
            Description: "desc",
            Steps: new List<CreatePipelineStepRequest>
            {
                new(StepType.Build, "build", 1, new Dictionary<string, string> { ["k"] = "v" }),
                new(StepType.Sign, "sign", 2, new Dictionary<string, string>())
            },
            Tags: new List<string> { "prod", "el9" },
            GitRepoUrl: "https://example.com/repo.git",
            GitBranch: "main",
            SpecPath: "pkg.spec",
            WebhookSecret: "s3cret",
            BuildImage: "lumina-rpm-build:latest",
            GitUsername: "user",
            GitToken: "tok",
            SpecContent: "Name: pkg");

        var rt = RoundTrip(req);
        Assert.Equal(req.Name, rt.Name);
        Assert.Equal(req.Steps.Count, rt.Steps.Count);
        Assert.Equal(StepType.Sign, rt.Steps[1].Type);
        Assert.Equal("v", rt.Steps[0].Configuration["k"]);
        Assert.Equal(2, rt.Tags.Count);
        Assert.Equal("s3cret", rt.WebhookSecret);
    }

    [Fact]
    public void UpdatePipelineRequest_RoundTrips()
    {
        var req = new UpdatePipelineRequest(
            "n", "d", new List<CreatePipelineStepRequest>(), new List<string>(),
            GitRepoUrl: "u", SpecContent: "c", ExpectedUpdatedAt: DateTime.UtcNow);
        var rt = RoundTrip(req);
        Assert.Equal("n", rt.Name);
        Assert.Equal("u", rt.GitRepoUrl);
        Assert.Equal(req.ExpectedUpdatedAt, rt.ExpectedUpdatedAt);
    }

    [Fact]
    public void TriggerBuildRequest_RoundTrips_AllFields()
    {
        var req = new TriggerBuildRequest(
            "pkg.spec", "content", "git://host/repo#branch=main",
            "webhook", "abc123", "main", "fix: bug", "jane");
        var rt = RoundTrip(req);
        Assert.Equal("pkg.spec", rt.SpecName);
        Assert.Equal("abc123", rt.CommitSha);
        Assert.Equal("fix: bug", rt.CommitMessage);
        Assert.Equal("jane", rt.CommitAuthor);
    }

    [Fact]
    public void TriggerBuildRequest_RoundTrips_RequiredOnly()
    {
        // Records with optional params must round-trip even when optionals are
        // omitted — a deserializer that can't reconstruct the default shape would
        // break every minimal request.
        var req = new TriggerBuildRequest("pkg.spec", "", null, "auto");
        var rt = RoundTrip(req);
        Assert.Equal("pkg.spec", rt.SpecName);
        Assert.Null(rt.CommitSha);
        Assert.Null(rt.Branch);
    }

    [Fact]
    public void Security_Dtos_RoundTrip()
    {
        RoundTrip(new CreateKeyRequest("k", "pub", "priv-ref", DateTime.UtcNow, "ops"));
        RoundTrip(new GenerateKeyRequest("k", "e@x.com", "pass"));
        RoundTrip(new SignPackageRequest(Guid.NewGuid(), Guid.NewGuid()));
        RoundTrip(new SignArtifactRequest(Guid.NewGuid(), "/path", Guid.NewGuid()));
        RoundTrip(new VerifySignatureRequest(Guid.NewGuid(), "sig"));
        RoundTrip(new ComputeHashRequest(Guid.NewGuid(), "/file"));
        RoundTrip(new StoreHashRequest(Guid.NewGuid(), "f.rpm", "sha", "md5", 123L));
    }

    [Fact]
    public void Repository_And_Source_Dtos_RoundTrip()
    {
        RoundTrip(new CreateRepositoryRequest("n", "disp", "el/9/baseos", "x86_64", "el9", "ops"));
        RoundTrip(new PublishPackageRequest(Guid.NewGuid(), Guid.NewGuid(), "ops"));
        RoundTrip(new SyncRepositoryRequest(Guid.NewGuid()));
        RoundTrip(new FetchSourceRequest("pkg"));
        RoundTrip(new FetchAllSourcesRequest());
        RoundTrip(new UpdateConfigRequest("content"));
        RoundTrip(new AddPackageToConfigRequest("n", "src", "git", "main", "img", "spec"));
    }

    // ─── Response DTOs ───────────────────────────────────────────────────

    [Fact]
    public void ApiResponse_RoundTrips_Success()
    {
        var resp = new ApiResponse<string>(true, "data", null, "ok");
        var rt = RoundTrip(resp);
        Assert.True(rt.Success);
        Assert.Equal("data", rt.Data);
        Assert.Equal("ok", rt.Message);
    }

    [Fact]
    public void ApiResponse_RoundTrips_Error_NullData()
    {
        var resp = new ApiResponse<string>(false, null, "boom", null);
        var rt = RoundTrip(resp);
        Assert.False(rt.Success);
        Assert.Null(rt.Data);
        Assert.Equal("boom", rt.Error);
    }

    [Fact]
    public void BuildJobResponse_RoundTrips_WithArtifacts()
    {
        var resp = new BuildJobResponse(
            Guid.NewGuid(), Guid.NewGuid(), BuildStatus.Queued, "pkg.spec",
            "cid", "logs", DateTime.UtcNow, null, null, "webhook",
            new List<BuildArtifactResponse>
            {
                new(Guid.NewGuid(), "a.rpm", 1024, "sha", "md5", "pgp", ScanStatus.Pending)
            },
            "git://src", "sha1", "main", "msg", "jane");
        var rt = RoundTrip(resp);
        Assert.Equal(BuildStatus.Queued, rt.Status);
        Assert.Single(rt.Artifacts);
        Assert.Equal("sha", rt.Artifacts[0].HashSha256);
        Assert.Equal(ScanStatus.Pending, rt.Artifacts[0].CveScanStatus);
    }

    [Fact]
    public void PipelineResponse_RoundTrips()
    {
        var resp = new PipelineResponse(
            Guid.NewGuid(), "n", "d", PipelineStatus.Active,
            new List<PipelineStepResponse>
            {
                new(Guid.NewGuid(), StepType.Build, "b", 1, StepStatus.Pending,
                    new Dictionary<string, string> { ["x"] = "y" })
            },
            "ops", DateTime.UtcNow, DateTime.UtcNow, new List<string> { "t" },
            "git://r", "main", "pkg.spec", "https://hook", "img", "user", true, "spec");
        var rt = RoundTrip(resp);
        Assert.Equal(PipelineStatus.Active, rt.Status);
        Assert.True(rt.HasGitToken);
        Assert.Single(rt.Steps);
        Assert.Equal("y", rt.Steps[0].Configuration["x"]);
    }

    [Fact]
    public void PackageResponse_FromEntity_RoundTrips()
    {
        // PackageResponse.From is the central entity→DTO projection; round-trip
        // its output to be sure internal-only fields (ArtifactId, StoragePath,
        // the Repository navigation) are not accidentally serialized.
        var package = new Package
        {
            Id = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            Name = "pkg",
            Version = "1.0",
            Release = "1",
            Arch = "x86_64",
            FileName = "pkg.rpm",
            FileSize = 2048,
            HashSha256 = "sha",
            CveScanStatus = ScanStatus.Clean,
            PublishedAt = DateTime.UtcNow,
            PublishedBy = "ops",
            PgpSignature = "pgp"
        };
        var dto = PackageResponse.From(package);

        var json = JsonSerializer.Serialize(dto, Options);
        // PackageResponse is a positional record that contains only the fields
        // listed in its constructor, so the projection cannot leak internal-only
        // entity fields by construction. Pin that explicitly: the JSON must not
        // contain the entity-only property *names* as quoted JSON keys.
        Assert.DoesNotContain("\"ArtifactId\"", json);
        Assert.DoesNotContain("\"StoragePath\"", json);
        Assert.DoesNotContain("\"Repository\"", json);

        var rt = JsonSerializer.Deserialize<PackageResponse>(json, Options)!;
        Assert.Equal("pkg", rt.Name);
        Assert.Equal("pgp", rt.PgpSignature);
    }

    [Fact]
    public void Paginated_List_Responses_RoundTrip()
    {
        var pipelines = new PipelineListResponse(
            new List<PipelineSummaryResponse>
            {
                new(Guid.NewGuid(), "a", "d", PipelineStatus.Active, "ops", DateTime.UtcNow, 2),
                new(Guid.NewGuid(), "b", "d", PipelineStatus.Draft, "ops", DateTime.UtcNow, 0)
            },
            TotalCount: 2, Page: 1, PageSize: 20);
        var rt = RoundTrip(pipelines);
        Assert.Equal(2, rt.TotalCount);
        Assert.Equal(2, rt.Pipelines.Count);

        var builds = new BuildListResponse(new List<BuildJobSummaryResponse>(), 0, 1, 20);
        var brt = RoundTrip(builds);
        Assert.Empty(brt.Builds);
    }

    [Fact]
    public void Dashboard_And_Queue_Responses_RoundTrip()
    {
        var dash = new DashboardStatsResponse(10, 2, 5, 1, 3, 42);
        var drt = RoundTrip(dash);
        Assert.Equal(42, drt.TotalPackages);

        var queue = new BuildQueueResponse(
            new List<BuildJobSummaryResponse>(), new List<BuildJobSummaryResponse>(), 0, 0);
        RoundTrip(queue);
    }

    [Fact]
    public void Source_Config_And_Download_Responses_RoundTrip()
    {
        // These DTOs back the SourceController responses that previously leaked
        // out as ad-hoc anonymous objects. Pin their shape so every source JSON
        // response flows through ApiResponse<T> the same way the rest of the API
        // does.
        var download = new SourceDownloadResponse("https://signed/url", "pkg", 2048L, "sha");
        var dlRt = RoundTrip(download);
        Assert.Equal("https://signed/url", dlRt.Url);
        Assert.Equal("pkg", dlRt.PackageName);
        Assert.Equal(2048L, dlRt.FileSize);

        var config = new SourceConfigResponse("Name: pkg");
        var cfgRt = RoundTrip(config);
        Assert.Equal("Name: pkg", cfgRt.Content);

        var mutationWithCount = new SourceConfigMutationResponse("Saved", 5);
        var mwcRt = RoundTrip(mutationWithCount);
        Assert.Equal("Saved", mwcRt.Message);
        Assert.Equal(5, mwcRt.Count);

        // Count is optional (add/remove return only a message) — must round-trip
        // to null, not default(int).
        var mutationNoCount = new SourceConfigMutationResponse("Package added");
        var mncRt = RoundTrip(mutationNoCount);
        Assert.Equal("Package added", mncRt.Message);
        Assert.Null(mncRt.Count);
    }
}
