using System.IO.Compression;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NuGetAssemblyMcp.Services.Models;

namespace NuGetAssemblyMcp.Services;

public class NuGetPackageService
{
    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".nuget-mcp", "cache");

    private static readonly string[] TfmPriority =
    [
        "net10.0", "net9.0", "net8.0", "net7.0", "net6.0",
        "netstandard2.1", "netstandard2.0", "netstandard1.6",
        "net48", "net472", "net471", "net47", "net462", "net461", "net46", "net45"
    ];

    private readonly SourceCacheContext _cacheContext;

    private readonly SourceRepository _repository;

    public NuGetPackageService()
    {
        var packageSource = new PackageSource("https://api.nuget.org/v3/index.json");
        _repository = Repository.Factory.GetCoreV3(packageSource);
        _cacheContext = new SourceCacheContext();
    }

    public async Task<IReadOnlyList<string>> ListVersionsAsync(string packageId, CancellationToken ct)
    {
        var resource = await _repository.GetResourceAsync<FindPackageByIdResource>(ct);
        var versions = await resource.GetAllVersionsAsync(packageId, _cacheContext, NullLogger.Instance, ct);

        return versions
            .OrderByDescending(v => v)
            .Select(v => v.ToNormalizedString())
            .ToList();
    }

    public async Task<PackageContent> LoadPackageAsync(
        string packageId,
        string? version = null,
        string? targetFramework = null,
        CancellationToken ct = default)
    {
        // Resolve version
        var resolvedVersion = await ResolveVersionAsync(packageId, version, ct);
        var packageDir = Path.Combine(CacheRoot, packageId.ToLowerInvariant(), resolvedVersion);

        // Check if already cached
        if (!Directory.Exists(packageDir) ||
            !Directory.GetFiles(packageDir, "*.dll", SearchOption.AllDirectories).Any())
            await DownloadAndExtractAsync(packageId, resolvedVersion, packageDir, ct);

        // Parse nuspec for repository metadata
        var (repositoryUrl, repositoryCommit) = ParseNuspec(packageDir, packageId);

        // Select best TFM
        var libDir = Path.Combine(packageDir, "lib");
        var selectedTfm = SelectTargetFramework(libDir, targetFramework);
        var tfmDir = Path.Combine(libDir, selectedTfm);

        // Find DLL, XML, PDB
        var dllPath = Directory.GetFiles(tfmDir, "*.dll")
                          .OrderByDescending(f => Path.GetFileNameWithoutExtension(f)
                              .Equals(packageId, StringComparison.OrdinalIgnoreCase)
                              ? 1
                              : 0)
                          .FirstOrDefault()
                      ?? throw new FileNotFoundException($"No DLL found in {tfmDir}");

        var baseName = Path.GetFileNameWithoutExtension(dllPath);
        var xmlDocPath = FindFile(tfmDir, $"{baseName}.xml");
        var pdbPath = FindFile(tfmDir, $"{baseName}.pdb")
                      ?? FindFile(packageDir, $"{baseName}.pdb");

        return new PackageContent(
            packageId,
            resolvedVersion,
            dllPath,
            xmlDocPath,
            pdbPath,
            selectedTfm,
            repositoryUrl,
            repositoryCommit
        );
    }

    private async Task<string> ResolveVersionAsync(string packageId, string? version, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(version)) return NuGetVersion.Parse(version).ToNormalizedString();

        var resource = await _repository.GetResourceAsync<FindPackageByIdResource>(ct);
        var versions = await resource.GetAllVersionsAsync(packageId, _cacheContext, NullLogger.Instance, ct);

        var latest = versions
                         .Where(v => !v.IsPrerelease)
                         .OrderByDescending(v => v)
                         .FirstOrDefault()
                     ?? versions.OrderByDescending(v => v).FirstOrDefault()
                     ?? throw new InvalidOperationException($"No versions found for package '{packageId}'");

        return latest.ToNormalizedString();
    }

    private async Task DownloadAndExtractAsync(
        string packageId,
        string version,
        string packageDir,
        CancellationToken ct)
    {
        Directory.CreateDirectory(packageDir);

        var resource = await _repository.GetResourceAsync<FindPackageByIdResource>(ct);
        var nupkgPath = Path.Combine(packageDir, $"{packageId}.{version}.nupkg");

        await using (var fileStream = File.Create(nupkgPath))
        {
            var success = await resource.CopyNupkgToStreamAsync(
                packageId,
                NuGetVersion.Parse(version),
                fileStream,
                _cacheContext,
                NullLogger.Instance,
                ct);

            if (!success)
                throw new InvalidOperationException(
                    $"Failed to download package '{packageId}' version '{version}'");
        }

        // Extract
        ZipFile.ExtractToDirectory(nupkgPath, packageDir, true);

        // Remove the nupkg to save space
        File.Delete(nupkgPath);
    }

    private static (string? RepositoryUrl, string? RepositoryCommit) ParseNuspec(
        string packageDir, string packageId)
    {
        var nuspecPath = Directory.GetFiles(packageDir, "*.nuspec", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        if (nuspecPath is null)
            return (null, null);

        try
        {
            var doc = XDocument.Load(nuspecPath);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var repo = doc.Root?
                .Element(ns + "metadata")?
                .Element(ns + "repository");

            if (repo is null)
                return (null, null);

            var url = repo.Attribute("url")?.Value;
            var commit = repo.Attribute("commit")?.Value;
            return (url, commit);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string SelectTargetFramework(string libDir, string? targetFramework)
    {
        if (!Directory.Exists(libDir))
            throw new DirectoryNotFoundException(
                "No 'lib' directory found. The package may not contain managed assemblies.");

        var availableTfms = Directory.GetDirectories(libDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Cast<string>()
            .ToList();

        if (availableTfms.Count == 0) throw new InvalidOperationException("No target framework folders found in lib/");

        // If a specific TFM was requested, try to match it
        if (!string.IsNullOrEmpty(targetFramework))
        {
            var match = availableTfms.FirstOrDefault(t =>
                t.Equals(targetFramework, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        // Select best TFM by priority
        foreach (var tfm in TfmPriority)
        {
            var match = availableTfms.FirstOrDefault(t => t.Equals(tfm, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        // Fallback: pick the first one alphabetically descending (likely highest version)
        return availableTfms.OrderByDescending(t => t).First();
    }

    private static string? FindFile(string directory, string fileName)
    {
        if (!Directory.Exists(directory))
            return null;

        var path = Path.Combine(directory, fileName);
        return File.Exists(path) ? path : null;
    }
}