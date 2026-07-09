using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NuGetAssemblyMcp.Services;

namespace NuGetAssemblyMcp.Tools;

[McpServerToolType]
public class PackageTools(NuGetPackageService packageService)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    [McpServerTool(Name = "set_working_directory")]
    [Description(
        "Sets the working directory for NuGet configuration lookup. " +
        "This allows loading solution-specific NuGet.config files that may define private feeds. " +
        "Call this before load_package or list_package_versions to use a specific solution's package sources.")]
    public string SetWorkingDirectory(
        [Description("The directory path to use for NuGet.config lookup. " +
                     "Typically the solution or project root directory. " +
                     "Pass an empty string or null to reset to default behavior.")]
        string? directoryPath)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(directoryPath) ? null : directoryPath;
        packageService.SetWorkingDirectory(normalizedPath);
        
        var result = new
        {
            Success = true,
            WorkingDirectory = packageService.CurrentWorkingDirectory,
            Message = packageService.CurrentWorkingDirectory is not null
                ? $"Working directory set to: {packageService.CurrentWorkingDirectory}"
                : "Working directory reset to default (user profile NuGet.config)"
        };
        
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "list_package_versions")]
    [Description("Lists all available versions of a NuGet package, ordered from newest to oldest.")]
    public async Task<string> ListPackageVersions(
        [Description("The NuGet package ID (e.g. 'Newtonsoft.Json')")]
        string packageId,
        CancellationToken ct)
    {
        var versions = await packageService.ListVersionsAsync(packageId, ct);
        return JsonSerializer.Serialize(versions, JsonOptions);
    }

    [McpServerTool(Name = "load_package")]
    [Description(
        "Downloads and caches a NuGet package, returning metadata about the loaded package " +
        "including the resolved version, selected target framework, repository URL, and available assemblies.")]
    public async Task<string> LoadPackage(
        [Description("The NuGet package ID (e.g. 'Newtonsoft.Json')")]
        string packageId,
        [Description("The package version to load. Defaults to the latest stable version if omitted.")]
        string? version = null,
        [Description(
            "The target framework moniker to select (e.g. 'net8.0'). If omitted, the best available TFM is selected automatically.")]
        string? targetFramework = null,
        CancellationToken ct = default)
    {
        var content = await packageService.LoadPackageAsync(packageId, version, targetFramework, ct);

        // SECURITY: Only expose safe metadata - never expose source URLs or credentials
        var result = new
        {
            content.PackageId,
            content.Version,
            content.TargetFramework,
            content.RepositoryUrl,
            content.RepositoryCommit,
            HasXmlDocs = content.XmlDocPath is not null,
            HasPdb = content.PdbPath is not null,
            AssemblyFile = Path.GetFileName(content.DllPath)
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "get_package_metadata")]
    [Description(
        "Gets detailed package metadata from NuGet sources, including description, authors, license, " +
        "dependencies, vulnerabilities, and deprecation status. Similar to what you'd see on NuGet.org.")]
    public async Task<string> GetPackageMetadata(
        [Description("The NuGet package ID (e.g. 'Newtonsoft.Json')")]
        string packageId,
        [Description("The package version. If omitted, returns metadata for the latest stable version.")]
        string? version = null,
        CancellationToken ct = default)
    {
        var metadata = await packageService.GetPackageMetadataAsync(packageId, version, ct);

        if (metadata is null)
        {
            return JsonSerializer.Serialize(new
            {
                Error = $"Package '{packageId}' not found" + (version is not null ? $" version '{version}'" : ""),
                PackageId = packageId,
                Version = version
            }, JsonOptions);
        }

        // SECURITY: Metadata is already sanitized by the service layer
        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    [McpServerTool(Name = "list_sources")]
    [Description(
        "Lists the currently configured NuGet package sources based on the working directory context. " +
        "Shows all sources from NuGet.config hierarchy (machine, user, and solution-level) plus the fallback source. " +
        "Use set_working_directory first to load solution-specific sources.")]
    public string ListSources()
    {
        var sources = packageService.GetConfiguredSources();

        // SECURITY: Source URLs are already sanitized by the service layer to remove credentials
        var result = new
        {
            WorkingDirectory = packageService.CurrentWorkingDirectory ?? "(default - user profile)",
            IsUsingLocalTooling = packageService.IsUsingLocalTooling,
            Sources = sources.Select(s => new
            {
                s.Name,
                s.Url,
                s.IsEnabled,
                s.IsOfficial,
                s.IsMachineWide,
                s.ProtocolVersion
            }).ToList()
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
