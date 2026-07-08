using NuGetAssemblyMcp.Services;
using Xunit.Abstractions;

namespace NuGetAssemblyMcp.Tests;

public class NuGetPackageServiceTests(ITestOutputHelper output)
{
    private readonly NuGetPackageService _service = new();

    [Fact]
    public async Task ListVersions_ReturnsVersionsForDuendeIdentityServer()
    {
        var versions = await _service.ListVersionsAsync("Duende.IdentityServer", CancellationToken.None);

        output.WriteLine($"Found {versions.Count} versions");
        output.WriteLine($"Latest 5: {string.Join(", ", versions.Take(5))}");

        Assert.NotNull(versions);
        Assert.NotEmpty(versions);
        Assert.True(versions.Count > 5);
        Assert.Contains(versions, v => v.StartsWith("7."));
    }

    [Fact]
    public async Task LoadPackage_DownloadsLatestDuendeIdentityServer()
    {
        var content = await _service.LoadPackageAsync("Duende.IdentityServer");

        output.WriteLine($"Package: {content.PackageId} v{content.Version}");
        output.WriteLine($"TFM: {content.TargetFramework}");
        output.WriteLine($"DLL: {content.DllPath}");
        output.WriteLine($"XML Docs: {content.XmlDocPath ?? "(none)"}");
        output.WriteLine($"PDB: {content.PdbPath ?? "(none)"}");
        output.WriteLine($"Repository: {content.RepositoryUrl ?? "(none)"}");
        output.WriteLine($"Commit: {content.RepositoryCommit ?? "(none)"}");

        Assert.NotNull(content);
        Assert.Equal("Duende.IdentityServer", content.PackageId);
        Assert.NotEmpty(content.Version);
        Assert.True(File.Exists(content.DllPath), $"DLL should exist at {content.DllPath}");
        Assert.NotEmpty(content.TargetFramework);
        Assert.Matches(@"^net\d+\.\d+$|^netstandard\d+\.\d+$", content.TargetFramework);
    }

    [Fact]
    public async Task LoadPackage_WithSpecificVersion()
    {
        var content = await _service.LoadPackageAsync("Duende.IdentityServer", "7.0.0");

        output.WriteLine($"Loaded: {content.PackageId} v{content.Version} [{content.TargetFramework}]");
        output.WriteLine($"DLL: {content.DllPath}");

        Assert.NotNull(content);
        Assert.Equal("Duende.IdentityServer", content.PackageId);
        Assert.Equal("7.0.0", content.Version);
        Assert.True(File.Exists(content.DllPath));
    }

    [Fact]
    public async Task LoadPackage_WithSpecificTfm()
    {
        var content = await _service.LoadPackageAsync(
            "Duende.IdentityServer", "7.0.0", "net8.0");

        output.WriteLine($"Loaded: {content.PackageId} v{content.Version} [{content.TargetFramework}]");
        output.WriteLine($"DLL: {content.DllPath}");

        Assert.NotNull(content);
        Assert.Equal("net8.0", content.TargetFramework);
        Assert.True(File.Exists(content.DllPath));
    }

    [Fact]
    public async Task LoadPackage_InvalidPackage_Throws()
    {
        output.WriteLine("Attempting to load non-existent package...");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.LoadPackageAsync("This.Package.Does.Not.Exist.XYZ.12345"));

        output.WriteLine($"Threw expected exception: {ex.Message}");
    }
}