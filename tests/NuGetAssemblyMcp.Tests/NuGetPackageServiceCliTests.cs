using NSubstitute;
using NuGetAssemblyMcp.Services;
using Xunit.Abstractions;

namespace NuGetAssemblyMcp.Tests;

/// <summary>
/// Unit tests for NuGetPackageService with mocked CLI runner.
/// Tests the fallback behavior and strategy selection.
/// </summary>
public class NuGetPackageServiceCliTests(ITestOutputHelper output)
{
    [Fact]
    public void IsUsingLocalTooling_WhenSdkAvailable_ReturnsTrue()
    {
        // Arrange
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(true);
        mockCliRunner.GlobalPackagesPath.Returns(GetTestGlobalPackagesPath());
        
        var service = new NuGetPackageService(mockCliRunner);
        
        // Act & Assert
        Assert.True(service.IsUsingLocalTooling);
        output.WriteLine("Service correctly reports using local tooling when SDK is available");
    }

    [Fact]
    public void IsUsingLocalTooling_WhenSdkNotAvailable_ReturnsFalse()
    {
        // Arrange
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(false);
        mockCliRunner.GlobalPackagesPath.Returns(GetTestGlobalPackagesPath());
        
        var service = new NuGetPackageService(mockCliRunner);
        
        // Act & Assert
        Assert.False(service.IsUsingLocalTooling);
        output.WriteLine("Service correctly reports NOT using local tooling when SDK is unavailable");
    }

    [Fact]
    public async Task LoadPackageAsync_WhenCliAvailableAndRestoreSucceeds_UsesGlobalCache()
    {
        // Arrange
        var globalCachePath = GetTestGlobalPackagesPath();
        var packageDir = Path.Combine(globalCachePath, "newtonsoft.json", "13.0.3");
        
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(true);
        mockCliRunner.GlobalPackagesPath.Returns(globalCachePath);
        
        // Simulate successful restore by creating the directory structure when RestorePackageAsync is called
        // This mimics what dotnet restore actually does
        mockCliRunner.RestorePackageAsync("Newtonsoft.Json", "13.0.3", Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                SetupMockPackageDirectory(packageDir, "Newtonsoft.Json");
                return Task.FromResult(true);
            });
        
        var service = new NuGetPackageService(mockCliRunner);
        
        try
        {
            // Act
            var content = await service.LoadPackageAsync("Newtonsoft.Json", "13.0.3");
            
            // Assert
            Assert.NotNull(content);
            Assert.Equal("Newtonsoft.Json", content.PackageId);
            Assert.Equal("13.0.3", content.Version);
            Assert.Contains(globalCachePath, content.DllPath);
            
            output.WriteLine($"Package loaded from global cache: {content.DllPath}");
            
            // Verify CLI was called
            await mockCliRunner.Received(1).RestorePackageAsync("Newtonsoft.Json", "13.0.3", Arg.Any<CancellationToken>());
        }
        finally
        {
            // Cleanup
            CleanupTestDirectory(globalCachePath);
        }
    }

    [Fact]
    public async Task LoadPackageAsync_WhenPackageAlreadyInGlobalCache_SkipsRestore()
    {
        // Arrange
        var globalCachePath = GetTestGlobalPackagesPath();
        var packageDir = Path.Combine(globalCachePath, "newtonsoft.json", "13.0.3");
        
        // Pre-create the package directory to simulate it already being in cache
        SetupMockPackageDirectory(packageDir, "Newtonsoft.Json");
        
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(true);
        mockCliRunner.GlobalPackagesPath.Returns(globalCachePath);
        
        var service = new NuGetPackageService(mockCliRunner);
        
        try
        {
            // Act
            var content = await service.LoadPackageAsync("Newtonsoft.Json", "13.0.3");
            
            // Assert
            Assert.NotNull(content);
            Assert.Equal("Newtonsoft.Json", content.PackageId);
            Assert.Contains(globalCachePath, content.DllPath);
            
            output.WriteLine($"Package loaded from existing cache (no restore needed): {content.DllPath}");
            
            // Verify CLI was NOT called since package was already cached
            await mockCliRunner.DidNotReceive().RestorePackageAsync(
                Arg.Any<string>(), 
                Arg.Any<string>(), 
                Arg.Any<CancellationToken>());
        }
        finally
        {
            // Cleanup
            CleanupTestDirectory(globalCachePath);
        }
    }

    [Fact]
    public async Task LoadPackageAsync_WhenCliNotAvailable_FallsBackToApi()
    {
        // Arrange
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(false);
        mockCliRunner.GlobalPackagesPath.Returns(GetTestGlobalPackagesPath());
        
        var service = new NuGetPackageService(mockCliRunner);
        
        // Act - This will use the API fallback since CLI is not available
        var content = await service.LoadPackageAsync("Newtonsoft.Json", "13.0.3");
        
        // Assert
        Assert.NotNull(content);
        Assert.Equal("Newtonsoft.Json", content.PackageId);
        Assert.Equal("13.0.3", content.Version);
        
        // Should NOT have called CLI restore
        await mockCliRunner.DidNotReceive().RestorePackageAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<CancellationToken>());
        
        output.WriteLine($"Package loaded via API fallback: {content.DllPath}");
    }

    [Fact]
    public async Task LoadPackageAsync_WhenCliRestoreFails_FallsBackToApi()
    {
        // Arrange
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(true);
        mockCliRunner.GlobalPackagesPath.Returns(GetTestGlobalPackagesPath());
        mockCliRunner.RestorePackageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false); // Simulate restore failure
        
        var service = new NuGetPackageService(mockCliRunner);
        
        // Act - Should fall back to API after CLI fails
        var content = await service.LoadPackageAsync("Newtonsoft.Json", "13.0.3");
        
        // Assert
        Assert.NotNull(content);
        Assert.Equal("Newtonsoft.Json", content.PackageId);
        
        // CLI was attempted
        await mockCliRunner.Received(1).RestorePackageAsync("Newtonsoft.Json", "13.0.3", Arg.Any<CancellationToken>());
        
        output.WriteLine($"Package loaded via API fallback after CLI failure: {content.DllPath}");
    }

    [Fact]
    public async Task ListVersionsAsync_QueriesConfiguredSources()
    {
        // Arrange
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(true);
        mockCliRunner.GlobalPackagesPath.Returns(GetTestGlobalPackagesPath());
        
        var service = new NuGetPackageService(mockCliRunner);
        
        // Act
        var versions = await service.ListVersionsAsync("Newtonsoft.Json", CancellationToken.None);
        
        // Assert
        Assert.NotNull(versions);
        Assert.NotEmpty(versions);
        Assert.Contains(versions, v => v.StartsWith("13."));
        
        output.WriteLine($"Found {versions.Count} versions, latest: {versions.FirstOrDefault()}");
    }

    private static string GetTestGlobalPackagesPath()
    {
        return Path.Combine(Path.GetTempPath(), "nuget-mcp-test-cache", Guid.NewGuid().ToString("N"));
    }

    private static void SetupMockPackageDirectory(string packageDir, string packageId)
    {
        var libDir = Path.Combine(packageDir, "lib", "net8.0");
        Directory.CreateDirectory(libDir);
        
        // Create a minimal DLL (just an empty file for testing path resolution)
        var dllPath = Path.Combine(libDir, $"{packageId}.dll");
        File.WriteAllBytes(dllPath, Array.Empty<byte>());
        
        // Create a minimal nuspec
        var nuspecPath = Path.Combine(packageDir, $"{packageId.ToLowerInvariant()}.nuspec");
        File.WriteAllText(nuspecPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{packageId}</id>
                <version>13.0.3</version>
              </metadata>
            </package>
            """);
    }

    private static void CleanupTestDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
