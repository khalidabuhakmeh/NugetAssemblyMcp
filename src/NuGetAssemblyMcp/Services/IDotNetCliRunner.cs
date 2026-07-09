namespace NuGetAssemblyMcp.Services;

/// <summary>
/// Abstraction for running dotnet CLI commands.
/// Enables mocking for unit tests and provides fallback detection.
/// </summary>
public interface IDotNetCliRunner
{
    /// <summary>
    /// Returns true if the dotnet SDK is available on this machine.
    /// Checked once at startup and cached.
    /// </summary>
    bool IsSdkAvailable { get; }
    
    /// <summary>
    /// Restores a NuGet package to the global cache using dotnet restore.
    /// </summary>
    /// <param name="packageId">The package ID to restore</param>
    /// <param name="version">The specific version to restore</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if restore succeeded, false otherwise</returns>
    Task<bool> RestorePackageAsync(string packageId, string version, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the path to the global NuGet packages cache.
    /// Typically ~/.nuget/packages on Unix or %USERPROFILE%\.nuget\packages on Windows.
    /// </summary>
    string GlobalPackagesPath { get; }
}
