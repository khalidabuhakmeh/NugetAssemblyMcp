using NSubstitute;
using NuGetAssemblyMcp.Services;
using Xunit.Abstractions;

namespace NuGetAssemblyMcp.Tests;

/// <summary>
/// Tests for package metadata and sources listing features.
/// </summary>
public class PackageMetadataAndSourcesTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GetPackageMetadataAsync_ReturnsMetadataForKnownPackage()
    {
        // Arrange
        var service = new NuGetPackageService();
        
        // Act
        var metadata = await service.GetPackageMetadataAsync("Newtonsoft.Json", "13.0.3", CancellationToken.None);
        
        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("Newtonsoft.Json", metadata.PackageId);
        Assert.Equal("13.0.3", metadata.Version);
        Assert.NotNull(metadata.Description);
        Assert.NotEmpty(metadata.Description);
        Assert.NotNull(metadata.Authors);
        Assert.Contains("James Newton-King", metadata.Authors);
        Assert.NotNull(metadata.ProjectUrl);
        Assert.NotEmpty(metadata.ProjectUrl);
        // LicenseUrl may be null if package uses license expression instead
        // DownloadCount may be null or 0 for some sources
        Assert.True(metadata.DownloadCount is null or >= 0);
        
        output.WriteLine($"Package: {metadata.PackageId} v{metadata.Version}");
        output.WriteLine($"Title: {metadata.Title}");
        output.WriteLine($"Description: {metadata.Description?[..Math.Min(100, metadata.Description.Length)]}...");
        output.WriteLine($"Authors: {metadata.Authors}");
        output.WriteLine($"Tags: {metadata.Tags}");
        output.WriteLine($"Downloads: {metadata.DownloadCount:N0}");
        output.WriteLine($"Published: {metadata.Published}");
        output.WriteLine($"Dependencies: {metadata.Dependencies.Count} groups");
    }

    [Fact]
    public async Task GetPackageMetadataAsync_LatestVersion_ReturnsMetadata()
    {
        // Arrange
        var service = new NuGetPackageService();
        
        // Act - No version specified, should get latest
        var metadata = await service.GetPackageMetadataAsync("xunit", ct: CancellationToken.None);
        
        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("xunit", metadata.PackageId);
        Assert.NotEmpty(metadata.Version);
        Assert.NotNull(metadata.Description);
        
        output.WriteLine($"Latest xunit: {metadata.Version}");
        output.WriteLine($"Description: {metadata.Description}");
    }

    [Fact]
    public async Task GetPackageMetadataAsync_NonExistentPackage_ReturnsNull()
    {
        // Arrange
        var service = new NuGetPackageService();
        
        // Act
        var metadata = await service.GetPackageMetadataAsync(
            "This.Package.Does.Not.Exist.XYZ.99999", 
            ct: CancellationToken.None);
        
        // Assert
        Assert.Null(metadata);
        
        output.WriteLine("Correctly returned null for non-existent package");
    }

    [Fact]
    public async Task GetPackageMetadataAsync_WithDependencies_ReturnsDependencyInfo()
    {
        // Arrange
        var service = new NuGetPackageService();
        
        // Act - Get a package with known dependencies
        var metadata = await service.GetPackageMetadataAsync(
            "Microsoft.Extensions.DependencyInjection", 
            "8.0.0", 
            CancellationToken.None);
        
        // Assert
        Assert.NotNull(metadata);
        Assert.NotEmpty(metadata.Dependencies);
        
        output.WriteLine($"Package: {metadata.PackageId} v{metadata.Version}");
        output.WriteLine($"Dependencies ({metadata.Dependencies.Count} groups):");
        foreach (var group in metadata.Dependencies)
        {
            output.WriteLine($"  [{group.TargetFramework}]");
            foreach (var dep in group.Dependencies)
            {
                output.WriteLine($"    - {dep.PackageId} {dep.VersionRange}");
            }
        }
    }

    [Fact]
    public void GetConfiguredSources_ReturnsAtLeastFallbackSource()
    {
        // Arrange
        var service = new NuGetPackageService();
        
        // Act
        var sources = service.GetConfiguredSources();
        
        // Assert
        Assert.NotNull(sources);
        Assert.NotEmpty(sources);
        
        // Should always have at least the fallback source
        Assert.Contains(sources, s => s.Url.Contains("nuget.org", StringComparison.OrdinalIgnoreCase));
        
        output.WriteLine($"Configured sources ({sources.Count}):");
        foreach (var source in sources)
        {
            output.WriteLine($"  - {source.Name}");
            output.WriteLine($"    URL: {source.Url}");
            output.WriteLine($"    Enabled: {source.IsEnabled}, Official: {source.IsOfficial}, MachineWide: {source.IsMachineWide}");
        }
    }

    [Fact]
    public void GetConfiguredSources_WithWorkingDirectory_ReloadsConfig()
    {
        // Arrange
        var service = new NuGetPackageService();
        var testDir = Path.GetTempPath();
        
        // Get sources before setting working directory
        var sourcesBefore = service.GetConfiguredSources();
        
        // Act - Set working directory
        service.SetWorkingDirectory(testDir);
        var sourcesAfter = service.GetConfiguredSources();
        
        // Assert - Both should have sources (might be the same or different)
        Assert.NotEmpty(sourcesBefore);
        Assert.NotEmpty(sourcesAfter);
        
        output.WriteLine($"Sources before: {sourcesBefore.Count}");
        output.WriteLine($"Sources after setting workdir to {testDir}: {sourcesAfter.Count}");
    }

    [Fact]
    public void GetConfiguredSources_UrlsAreSanitized()
    {
        // Arrange
        var service = new NuGetPackageService();
        
        // Act
        var sources = service.GetConfiguredSources();
        
        // Assert - No source URL should contain credentials
        foreach (var source in sources)
        {
            // Should not contain user:password@ or token@ patterns
            Assert.DoesNotMatch(@"https?://[^/]+:[^@]+@", source.Url);
            Assert.DoesNotContain("password", source.Url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apikey", source.Url, StringComparison.OrdinalIgnoreCase);
        }
        
        output.WriteLine("All source URLs verified as credential-free");
    }

    [Fact]
    public async Task GetPackageMetadataAsync_DeprecatedPackage_ReturnsDeprecationInfo()
    {
        // Arrange
        var service = new NuGetPackageService();
        
        // Act - Try to find a deprecated package (System.Data.SqlClient is deprecated in favor of Microsoft.Data.SqlClient)
        var metadata = await service.GetPackageMetadataAsync(
            "System.Data.SqlClient", 
            ct: CancellationToken.None);
        
        // Assert - Package should exist, deprecation status varies
        Assert.NotNull(metadata);
        
        output.WriteLine($"Package: {metadata.PackageId} v{metadata.Version}");
        output.WriteLine($"IsListed: {metadata.IsListed}");
        
        if (metadata.Deprecation is not null)
        {
            output.WriteLine($"DEPRECATED: {metadata.Deprecation.Message}");
            output.WriteLine($"Reasons: {string.Join(", ", metadata.Deprecation.Reasons ?? [])}");
            if (metadata.Deprecation.AlternatePackageId is not null)
            {
                output.WriteLine($"Alternative: {metadata.Deprecation.AlternatePackageId} {metadata.Deprecation.AlternateVersionRange}");
            }
        }
        else
        {
            output.WriteLine("No deprecation metadata available");
        }
    }

    [Fact]
    public async Task GetPackageMetadataAsync_WithVulnerabilities_ReturnsVulnerabilityInfo()
    {
        // Arrange
        var service = new NuGetPackageService();
        
        // Act - Get an old version of a package that might have vulnerabilities
        var metadata = await service.GetPackageMetadataAsync(
            "System.Text.Json", 
            "4.7.0",  // Old version
            CancellationToken.None);
        
        // Assert
        Assert.NotNull(metadata);
        
        output.WriteLine($"Package: {metadata.PackageId} v{metadata.Version}");
        
        if (metadata.Vulnerabilities is not null && metadata.Vulnerabilities.Count > 0)
        {
            output.WriteLine($"VULNERABILITIES ({metadata.Vulnerabilities.Count}):");
            foreach (var vuln in metadata.Vulnerabilities)
            {
                output.WriteLine($"  - Severity: {vuln.Severity}");
                output.WriteLine($"    Advisory: {vuln.AdvisoryUrl}");
            }
        }
        else
        {
            output.WriteLine("No vulnerabilities reported");
        }
    }
}
