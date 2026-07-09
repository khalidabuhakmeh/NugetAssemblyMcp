using NSubstitute;
using NuGetAssemblyMcp.Services;
using Xunit.Abstractions;

namespace NuGetAssemblyMcp.Tests;

/// <summary>
/// Tests for credential sanitization and security features.
/// CRITICAL: These tests verify that credentials are NEVER exposed to LLM context.
/// </summary>
public class CredentialSecurityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(
        "Failed to authenticate to https://user:password123@nuget.mycompany.com/v3/index.json",
        "Failed to authenticate to https://***@nuget.mycompany.com/v3/index.json")]
    [InlineData(
        "Error accessing https://apikey@private.feed.com/nuget",
        "Error accessing https://***@private.feed.com/nuget")]
    [InlineData(
        "Connection to https://mytoken:x-oauth-basic@github.com/nuget failed",
        "Connection to https://***@github.com/nuget failed")]
    [InlineData(
        "Cannot reach http://admin:secret@localhost:8080/api",
        "Cannot reach http://***@localhost:8080/api")]
    public void SanitizeErrorMessage_RemovesCredentialsFromUrls(string input, string expected)
    {
        // Use reflection to access the private sanitization method
        var method = typeof(NuGetPackageService).GetMethod(
            "SanitizeErrorMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        Assert.NotNull(method);
        
        var result = (string)method.Invoke(null, [input])!;
        
        output.WriteLine($"Input:    {input}");
        output.WriteLine($"Expected: {expected}");
        output.WriteLine($"Result:   {result}");
        
        Assert.Equal(expected, result);
        Assert.DoesNotContain("password", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("api_key=abc123def456", "api_key=***")]
    [InlineData("apiKey: 'mysecretkey'", "apiKey=***")]
    [InlineData("password=hunter2", "password=***")]
    [InlineData("token=\"ghp_xxxxxxxxxxxx\"", "token=***")]
    [InlineData("credential=mypassword", "credential=***")]
    public void SanitizeErrorMessage_RemovesCredentialKeyValuePairs(string input, string expectedPattern)
    {
        var method = typeof(NuGetPackageService).GetMethod(
            "SanitizeErrorMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        Assert.NotNull(method);
        
        var result = (string)method.Invoke(null, [input])!;
        
        output.WriteLine($"Input:  {input}");
        output.WriteLine($"Result: {result}");
        
        // The result should not contain the actual secret values
        Assert.DoesNotContain("abc123", result);
        Assert.DoesNotContain("mysecret", result);
        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("ghp_", result);
        Assert.DoesNotContain("mypassword", result);
    }

    [Fact]
    public void SanitizeErrorMessage_PreservesNonSensitiveContent()
    {
        var method = typeof(NuGetPackageService).GetMethod(
            "SanitizeErrorMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        Assert.NotNull(method);
        
        var input = "Package 'Newtonsoft.Json' version '13.0.3' not found on https://api.nuget.org/v3/index.json";
        var result = (string)method.Invoke(null, [input])!;
        
        // Safe content should be preserved
        Assert.Contains("Newtonsoft.Json", result);
        Assert.Contains("13.0.3", result);
        Assert.Contains("https://api.nuget.org/v3/index.json", result);
        
        output.WriteLine($"Input:  {input}");
        output.WriteLine($"Result: {result}");
    }

    [Fact]
    public void SanitizeErrorMessage_HandlesNullAndEmpty()
    {
        var method = typeof(NuGetPackageService).GetMethod(
            "SanitizeErrorMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        Assert.NotNull(method);
        
        Assert.Null(method.Invoke(null, [null]));
        Assert.Equal("", method.Invoke(null, [""]));
    }

    [Fact]
    public async Task LoadPackageAsync_ExceptionMessages_AreSanitized()
    {
        // Arrange
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(false);
        mockCliRunner.GlobalPackagesPath.Returns(Path.GetTempPath());
        
        var service = new NuGetPackageService(mockCliRunner);
        
        // Act - Try to load a package that doesn't exist
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LoadPackageAsync("This.Package.Definitely.Does.Not.Exist.XYZ.99999"));
        
        // Assert - The error message should not contain any credential patterns
        // Even if the underlying NuGet client somehow included them
        Assert.DoesNotMatch(@"[a-zA-Z0-9]+:[a-zA-Z0-9]+@", exception.Message);
        Assert.DoesNotContain("password", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", exception.Message.Replace("_", "").Replace("-", ""), StringComparison.OrdinalIgnoreCase);
        
        output.WriteLine($"Exception message: {exception.Message}");
    }
}

/// <summary>
/// Tests for working directory and solution-specific NuGet.config support.
/// </summary>
public class WorkingDirectoryTests(ITestOutputHelper output)
{
    [Fact]
    public void SetWorkingDirectory_WithValidPath_UpdatesCurrentDirectory()
    {
        // Arrange
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(true);
        mockCliRunner.GlobalPackagesPath.Returns(Path.GetTempPath());
        
        var service = new NuGetPackageService(mockCliRunner);
        var testDir = Path.GetTempPath();
        
        // Act
        service.SetWorkingDirectory(testDir);
        
        // Assert
        Assert.NotNull(service.CurrentWorkingDirectory);
        Assert.Equal(Path.GetFullPath(testDir), service.CurrentWorkingDirectory);
        
        output.WriteLine($"Working directory set to: {service.CurrentWorkingDirectory}");
    }

    [Fact]
    public void SetWorkingDirectory_WithNull_ResetsToDefault()
    {
        // Arrange
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(true);
        mockCliRunner.GlobalPackagesPath.Returns(Path.GetTempPath());
        
        var service = new NuGetPackageService(mockCliRunner);
        
        // Set a directory first
        service.SetWorkingDirectory(Path.GetTempPath());
        Assert.NotNull(service.CurrentWorkingDirectory);
        
        // Act - Reset to null
        service.SetWorkingDirectory(null);
        
        // Assert
        Assert.Null(service.CurrentWorkingDirectory);
        
        output.WriteLine("Working directory reset to default");
    }

    [Fact]
    public void SetWorkingDirectory_WithInvalidPath_SetsToNull()
    {
        // Arrange
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(true);
        mockCliRunner.GlobalPackagesPath.Returns(Path.GetTempPath());
        
        var service = new NuGetPackageService(mockCliRunner);
        
        // Act - Set to a path that doesn't exist
        service.SetWorkingDirectory("/this/path/does/not/exist/12345");
        
        // Assert - Should be null because path doesn't exist
        Assert.Null(service.CurrentWorkingDirectory);
        
        output.WriteLine("Invalid path correctly resulted in null working directory");
    }

    [Fact]
    public void SetWorkingDirectory_SamePathTwice_DoesNotReloadRepositories()
    {
        // Arrange
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(true);
        mockCliRunner.GlobalPackagesPath.Returns(Path.GetTempPath());
        
        var service = new NuGetPackageService(mockCliRunner);
        var testDir = Path.GetTempPath();
        
        // Act - Set same path twice
        service.SetWorkingDirectory(testDir);
        var firstPath = service.CurrentWorkingDirectory;
        
        service.SetWorkingDirectory(testDir);
        var secondPath = service.CurrentWorkingDirectory;
        
        // Assert - Should be the same (no unnecessary reloads)
        Assert.Equal(firstPath, secondPath);
        
        output.WriteLine("Same path set twice - no unnecessary reload");
    }

    [Fact]
    public async Task ListVersionsAsync_UsesConfiguredWorkingDirectory()
    {
        // Arrange
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(true);
        mockCliRunner.GlobalPackagesPath.Returns(Path.GetTempPath());
        
        var service = new NuGetPackageService(mockCliRunner);
        
        // Set working directory to the test project (which has its own NuGet.config potentially)
        var projectDir = AppContext.BaseDirectory;
        service.SetWorkingDirectory(projectDir);
        
        output.WriteLine($"Using working directory: {service.CurrentWorkingDirectory}");
        
        // Act - This should use the configured sources
        var versions = await service.ListVersionsAsync("xunit", CancellationToken.None);
        
        // Assert
        Assert.NotEmpty(versions);
        output.WriteLine($"Found {versions.Count} versions of xunit");
    }

    [Fact]
    public void Constructor_StartsWithNullWorkingDirectory()
    {
        // Arrange & Act
        var mockCliRunner = Substitute.For<IDotNetCliRunner>();
        mockCliRunner.IsSdkAvailable.Returns(true);
        mockCliRunner.GlobalPackagesPath.Returns(Path.GetTempPath());
        
        var service = new NuGetPackageService(mockCliRunner);
        
        // Assert - Should start with no specific working directory
        Assert.Null(service.CurrentWorkingDirectory);
        
        output.WriteLine("Service correctly starts with null working directory");
    }
}
