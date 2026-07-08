using NuGetAssemblyMcp.Services;

namespace NuGetAssemblyMcp.Tests;

public class SourceLinkServiceTests
{
    private readonly SourceLinkService _service = new();

    [Fact]
    public void GetSourceLinkInfo_WithRepositoryUrl_BuildsBrowseUrl()
    {
        var info = _service.GetSourceLinkInfo(
            null,
            "https://github.com/DuendeSoftware/IdentityServer.git",
            "abc123def456");

        Assert.NotNull(info);
        Assert.Equal("https://github.com/DuendeSoftware/IdentityServer.git", info.RepositoryUrl);
        Assert.Equal("abc123def456", info.Commit);
        Assert.NotNull(info.RepositoryBrowseUrl);
        Assert.Contains("github.com/DuendeSoftware/IdentityServer", info.RepositoryBrowseUrl);
        Assert.Contains("tree/abc123def456", info.RepositoryBrowseUrl);
    }

    [Fact]
    public void GetSourceLinkInfo_NullInputs_ReturnsNull()
    {
        var info = _service.GetSourceLinkInfo(
            null,
            null,
            null);

        Assert.Null(info);
    }
}