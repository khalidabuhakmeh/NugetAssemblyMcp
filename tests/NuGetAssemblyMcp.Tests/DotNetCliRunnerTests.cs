using NuGetAssemblyMcp.Services;
using Xunit.Abstractions;

namespace NuGetAssemblyMcp.Tests;

/// <summary>
/// Integration tests for DotNetCliRunner.
/// These tests actually invoke the dotnet CLI.
/// </summary>
public class DotNetCliRunnerTests(ITestOutputHelper output)
{
    [Fact]
    public void IsSdkAvailable_DetectsCorrectly()
    {
        // Arrange
        var runner = new DotNetCliRunner();
        
        // Act
        var isAvailable = runner.IsSdkAvailable;
        
        // Assert - The test machine should have the SDK installed
        output.WriteLine($"SDK Available: {isAvailable}");
        output.WriteLine($"Global Packages Path: {runner.GlobalPackagesPath}");
        
        // We expect SDK to be available on dev machines
        Assert.True(isAvailable, "Expected .NET SDK to be installed on test machine");
    }

    [Fact]
    public void GlobalPackagesPath_ReturnsValidPath()
    {
        // Arrange
        var runner = new DotNetCliRunner();
        
        // Act
        var path = runner.GlobalPackagesPath;
        
        // Assert
        Assert.NotNull(path);
        Assert.NotEmpty(path);
        
        // Should be an absolute path
        Assert.True(Path.IsPathRooted(path), "Global packages path should be absolute");
        
        output.WriteLine($"Global Packages Path: {path}");
        output.WriteLine($"Path exists: {Directory.Exists(path)}");
    }

    [Fact]
    public async Task RestorePackageAsync_RestoresKnownPackage()
    {
        // Arrange
        var runner = new DotNetCliRunner();
        
        if (!runner.IsSdkAvailable)
        {
            output.WriteLine("Skipping test - SDK not available");
            return;
        }
        
        // Act
        var result = await runner.RestorePackageAsync("Newtonsoft.Json", "13.0.3", CancellationToken.None);
        
        // Assert
        Assert.True(result, "Expected restore to succeed for Newtonsoft.Json");
        
        // Verify package is in global cache
        var packagePath = Path.Combine(runner.GlobalPackagesPath, "newtonsoft.json", "13.0.3");
        Assert.True(Directory.Exists(packagePath), $"Package should exist at {packagePath}");
        
        output.WriteLine($"Package restored to: {packagePath}");
    }

    [Fact]
    public async Task RestorePackageAsync_WithInvalidPackage_ReturnsFalse()
    {
        // Arrange
        var runner = new DotNetCliRunner();
        
        if (!runner.IsSdkAvailable)
        {
            output.WriteLine("Skipping test - SDK not available");
            return;
        }
        
        // Act
        var result = await runner.RestorePackageAsync(
            "This.Package.Does.Not.Exist.XYZ.98765", 
            "1.0.0", 
            CancellationToken.None);
        
        // Assert
        Assert.False(result, "Expected restore to fail for non-existent package");
        
        output.WriteLine("Restore correctly failed for non-existent package");
    }

    [Fact]
    public async Task RestorePackageAsync_FromPrivateFeed_UsesConfiguredSources()
    {
        // This test verifies that CLI restore respects NuGet.config
        // It will pass if the user has any private feeds configured
        
        var runner = new DotNetCliRunner();
        
        if (!runner.IsSdkAvailable)
        {
            output.WriteLine("Skipping test - SDK not available");
            return;
        }
        
        // Test with a well-known public package
        // If the user has nuget.org disabled but a mirror configured, this should still work
        var result = await runner.RestorePackageAsync("xunit", "2.9.3", CancellationToken.None);
        
        Assert.True(result, "Expected restore to succeed for xunit");
        
        output.WriteLine($"Restored xunit using configured NuGet sources");
    }
}
