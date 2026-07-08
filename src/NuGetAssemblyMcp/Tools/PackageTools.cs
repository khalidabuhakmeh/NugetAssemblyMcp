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
}