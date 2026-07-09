using System.Diagnostics;

namespace NuGetAssemblyMcp.Services;

/// <summary>
/// Runs dotnet CLI commands for NuGet package operations.
/// Uses temporary project files for restore operations and cleans up on startup.
/// </summary>
public class DotNetCliRunner : IDotNetCliRunner
{
    private const string TempDirPrefix = "nuget-mcp-restore-";
    private readonly Lazy<bool> _sdkAvailable;
    private readonly Lazy<string> _globalPackagesPath;

    public DotNetCliRunner()
    {
        _sdkAvailable = new Lazy<bool>(CheckSdkAvailability);
        _globalPackagesPath = new Lazy<string>(ResolveGlobalPackagesPath);
        
        // Clean up any stale temp directories from previous runs
        CleanupStaleTempDirectories();
    }

    public bool IsSdkAvailable => _sdkAvailable.Value;

    public string GlobalPackagesPath => _globalPackagesPath.Value;

    public async Task<bool> RestorePackageAsync(string packageId, string version, CancellationToken ct = default)
    {
        if (!IsSdkAvailable)
            return false;

        var tempDir = Path.Combine(Path.GetTempPath(), $"{TempDirPrefix}{Guid.NewGuid():N}");
        
        try
        {
            Directory.CreateDirectory(tempDir);
            
            // Create a minimal project file that references the package
            var projectPath = Path.Combine(tempDir, "restore.csproj");
            var projectContent = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                    <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <RestorePackagesPath>{GlobalPackagesPath}</RestorePackagesPath>
                    </PropertyGroup>
                    <ItemGroup>
                        <PackageReference Include="{packageId}" Version="{version}" />
                    </ItemGroup>
                </Project>
                """;
            
            await File.WriteAllTextAsync(projectPath, projectContent, ct);
            
            // Run dotnet restore
            var result = await RunDotNetCommandAsync("restore", projectPath, tempDir, ct);
            return result.ExitCode == 0;
        }
        finally
        {
            // Clean up temp directory
            TryDeleteDirectory(tempDir);
        }
    }

    private static bool CheckSdkAvailability()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-sdks",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(10));

            // SDK is available if the command succeeds and returns at least one SDK
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveGlobalPackagesPath()
    {
        // Try to get the path from dotnet nuget locals
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "nuget locals global-packages --list",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(TimeSpan.FromSeconds(10));

                if (process.ExitCode == 0)
                {
                    // Output format: "global-packages: /path/to/packages"
                    var parts = output.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var path = parts[1].Trim();
                        if (Directory.Exists(path))
                            return path;
                    }
                }
            }
        }
        catch
        {
            // Fall through to default path
        }

        // Default fallback path
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunDotNetCommandAsync(
        string command,
        string projectPath,
        string workingDirectory,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{command} \"{projectPath}\" --verbosity quiet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static void CleanupStaleTempDirectories()
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var staleDirs = Directory.GetDirectories(tempPath, $"{TempDirPrefix}*");

            foreach (var dir in staleDirs)
            {
                TryDeleteDirectory(dir);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static void TryDeleteDirectory(string path)
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
            // Ignore deletion errors - OS will clean up temp eventually
        }
    }
}
