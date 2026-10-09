using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Bitbound.SystemAbstractions.TestUtilities.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Updates;
using Xunit;

namespace QwenTray.Tests;

public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("v2026.9.26.0244", 2026, 9, 26, 244)]
    [InlineData("2026.9.26.0244", 2026, 9, 26, 244)]
    [InlineData("v1.2.3", 1, 2, 3, 0)]
    [InlineData("1.2", 1, 2, 0, 0)]
    [InlineData("v2026.9.26.0244-beta", 2026, 9, 26, 244)]
    [InlineData("1.2.3+build.5", 1, 2, 3, 0)]
    public void ATagParsesToItsLeadingNumbers(string tag, int major, int minor, int build, int revision)
    {
        Assert.True(UpdateVersionParser.TryParse(tag, out var version));
        Assert.Equal(new Version(major, minor, build, revision), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v-next")]
    public void JunkTagsAreRejected(string tag)
    {
        Assert.False(UpdateVersionParser.TryParse(tag, out _));
    }

    [Fact]
    public void APartialCurrentVersionSortsBelowAFullTag()
    {
        // "2026.9.26" leaves Revision at -1, which without normalization would sort above a ".0" release.
        Assert.True(UpdateVersionParser.TryParse("v2026.9.26.1", out var release));
        var current = UpdateVersionParser.NormalizeCurrent(new Version(2026, 9, 26));

        Assert.True(release > current);
    }
}

public sealed class ReleaseAssetTests
{
    [Fact]
    public void TheX64BuildPrefersItsSuffixedAssetAndFallsBackToTheBareExeName()
    {
        Assert.Equal("QwenTray-x64.exe", ReleaseAssetSelector.BuildAssetName(Architecture.X64));
        Assert.Equal(["QwenTray-x64.exe", "QwenTray.exe"], ReleaseAssetSelector.BuildCandidateNames(Architecture.X64));
    }

    [Fact]
    public void TheSuffixedAssetWinsOverTheBareOne()
    {
        var release = new GitHubRelease
        {
            TagName = "v2026.9.26.0244",
            Assets =
            [
                new GitHubReleaseAsset { Name = "QwenTray.exe", State = "uploaded", DownloadUrl = "https://example/bare" },
                new GitHubReleaseAsset { Name = "QwenTray-x64.exe", State = "uploaded", DownloadUrl = "https://example/suffixed" },
            ],
        };

        Assert.Equal("https://example/suffixed", ReleaseAssetSelector.Select(release)?.DownloadUrl);
    }

    [Fact]
    public void TheBareNameIsUsedWhenTheSuffixedAssetIsAbsent()
    {
        var release = new GitHubRelease
        {
            Assets = [new GitHubReleaseAsset { Name = "QwenTray.exe", State = "uploaded", DownloadUrl = "https://example/bare" }],
        };

        Assert.Equal("https://example/bare", ReleaseAssetSelector.Select(release)?.DownloadUrl);
    }

    [Fact]
    public void AnInFlightAssetIsUsedOnlyWhenNothingIsFinished()
    {
        var release = new GitHubRelease
        {
            Assets = [new GitHubReleaseAsset { Name = "QwenTray-x64.exe", State = "open", DownloadUrl = "https://example/open" }],
        };

        Assert.Equal("https://example/open", ReleaseAssetSelector.Select(release)?.DownloadUrl);
    }

    [Fact]
    public void AReleaseWithNoMatchingAssetSelectsNothing()
    {
        var release = new GitHubRelease { Assets = [new GitHubReleaseAsset { Name = "source.zip", State = "uploaded" }] };

        Assert.Null(ReleaseAssetSelector.Select(release));
    }
}

public sealed class UpdateHandoffTests
{
    [Fact]
    public void TheHandoffArgumentsRoundTrip()
    {
        var handoff = new UpdateHandoff(@"C:\Users\me\Apps\QwenTray.exe", 4321);
        var args = UpdateHandoff.BuildArguments(handoff);

        Assert.Equal(["--qwentray-update-target", @"C:\Users\me\Apps\QwenTray.exe", "--qwentray-update-pid", "4321"], args);
        Assert.Equal(handoff, UpdateHandoff.TryParse(args));
    }

    [Fact]
    public void RelaunchingStripsTheHandoffArguments()
    {
        var args = new[] { "unrelated", "--qwentray-update-target", @"C:\app.exe", "--qwentray-update-pid", "9", "--flag" };

        Assert.Equal(["unrelated", "--flag"], UpdateHandoff.Strip(args));
    }

    [Fact]
    public void NoArgumentsIsNotAHandoff()
    {
        Assert.Null(UpdateHandoff.TryParse([]));
    }

    [Fact]
    public void ATargetWithoutAProcessIdIsNotAHandoff()
    {
        Assert.Null(UpdateHandoff.TryParse(["--qwentray-update-target", @"C:\app.exe"]));
    }

    [Fact]
    public void AnEmptyTargetIsNotAHandoff()
    {
        Assert.Null(UpdateHandoff.TryParse(["--qwentray-update-target", "", "--qwentray-update-pid", "5"]));
    }
}

public sealed class UpdateDigestTests
{
    [Fact]
    public void ASha256DigestParses()
    {
        Assert.True(UpdateDigest.TryParse("sha256:" + new string('a', 64), out var expected));
        Assert.Equal(32, expected.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5:abc")]
    [InlineData("sha256:nothex")]
    [InlineData("sha256:abcd")]
    public void AnUnusableDigestIsRejected(string? digest)
    {
        Assert.False(UpdateDigest.TryParse(digest, out _));
    }

    [Fact]
    public async Task AFileIsCheckedAgainstItsDigest()
    {
        var bytes = Encoding.UTF8.GetBytes("qwentray-update-bytes");
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile("/QwenTray-x64.exe", bytes, System.IO.FileAttributes.Normal, null);

        Assert.True(await UpdateDigest.MatchesAsync("/QwenTray-x64.exe", SHA256.HashData(bytes), fileSystem, CancellationToken.None));
        Assert.False(await UpdateDigest.MatchesAsync("/QwenTray-x64.exe", new byte[32], fileSystem, CancellationToken.None));
    }
}

public sealed class GitHubReleaseUpdateServiceTests
{
    // A finished x64 asset with a well-formed digest, as the release workflow publishes it.
    private const string ReleaseJson = """
    {
      "tag_name": "v2026.10.9.0200",
      "assets": [
        {
          "name": "QwenTray-x64.exe",
          "state": "uploaded",
          "browser_download_url": "https://example/QwenTray-x64.exe",
          "digest": "sha256:00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"
        }
      ]
    }
    """;

    private static AppInfo Info(string version) => new("QwenTray", "bitbound/QwenTray", Version.Parse(version));

    private static GitHubReleaseUpdateService CreateService(AppInfo appInfo, HttpMessageHandler handler)
        => new(new HttpClient(handler), appInfo, new FakeFileSystem(), null!, NullLogger<GitHubReleaseUpdateService>.Instance);

    [Fact]
    public async Task ANewerReleaseIsReportedAndTheRequestCarriesAUserAgent()
    {
        var handler = new StubHandler(ReleaseJson);
        var service = CreateService(Info("2026.1.1.0"), handler);

        var update = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal("v2026.10.9.0200", update!.Version);
        Assert.Equal("QwenTray-x64.exe", update.AssetName);
        Assert.Equal("https://example/QwenTray-x64.exe", update.DownloadUrl);
        Assert.Contains("QwenTray", handler.LastRequest!.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCurrentVersionIsNotOfferedAsAnUpdate()
    {
        var service = CreateService(Info("2026.10.9.0200"), new StubHandler(ReleaseJson));

        Assert.Null(await service.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheUpdateAvailableEventFiresOnce()
    {
        var service = CreateService(Info("2026.1.1.0"), new StubHandler(ReleaseJson));
        var raised = 0;
        service.UpdateAvailable += (_, _) => raised++;

        await service.CheckAsync(TestContext.Current.CancellationToken);
        await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task AnHttpFailureDegradesToNoUpdate()
    {
        var service = CreateService(Info("2026.1.1.0"), new StubHandler("{}", HttpStatusCode.NotFound));

        Assert.Null(await service.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnrecognizedTagDegradesToNoUpdate()
    {
        var service = CreateService(Info("2026.1.1.0"), new StubHandler("""{ "tag_name": "nightly" }"""));

        Assert.Null(await service.CheckAsync(TestContext.Current.CancellationToken));
    }

    private sealed class StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;

            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            return Task.FromResult(response);
        }
    }
}
