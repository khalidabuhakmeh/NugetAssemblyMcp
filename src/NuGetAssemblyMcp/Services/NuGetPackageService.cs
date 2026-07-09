using System.IO.Compression;
using System.Text.RegularExpressions;
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
    // Fallback cache for when CLI is not available
    private static readonly string FallbackCacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".nuget-mcp", "cache");

    private static readonly string[] TfmPriority =
    [
        "net10.0", "net9.0", "net8.0", "net7.0", "net6.0",
        "netstandard2.1", "netstandard2.0", "netstandard1.6",
        "net48", "net472", "net471", "net47", "net462", "net461", "net46", "net45"
    ];

    // Regex to detect and sanitize credentials in URLs
    // Matches patterns like: https://user:password@host or https://token@host
    private static readonly Regex CredentialInUrlPattern = new(
        @"(https?://)([^/@]+@)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SourceCacheContext _cacheContext;
    private readonly SourceRepository _fallbackRepository;
    private readonly IDotNetCliRunner _cliRunner;
    
    // Repositories are loaded lazily per working directory to support solution-specific configs
    private string? _currentWorkingDirectory;
    private IReadOnlyList<SourceRepository>? _repositories;
    private readonly object _repositoryLock = new();

    public NuGetPackageService() : this(new DotNetCliRunner())
    {
    }

    public NuGetPackageService(IDotNetCliRunner cliRunner)
    {
        _cliRunner = cliRunner;
        _cacheContext = new SourceCacheContext();
        
        // Fallback to nuget.org for API-only mode (no credentials needed)
        var fallbackSource = new PackageSource("https://api.nuget.org/v3/index.json");
        _fallbackRepository = Repository.Factory.GetCoreV3(fallbackSource);
    }

    /// <summary>
    /// Indicates whether the service is using local CLI tooling (true) or API fallback (false).
    /// </summary>
    public bool IsUsingLocalTooling => _cliRunner.IsSdkAvailable;

    /// <summary>
    /// Sets the working directory for loading NuGet.config files.
    /// This allows loading solution-specific package sources.
    /// </summary>
    /// <param name="workingDirectory">The directory to use as root for config lookup, or null to use default.</param>
    public void SetWorkingDirectory(string? workingDirectory)
    {
        lock (_repositoryLock)
        {
            // Normalize and validate the path
            var normalizedPath = workingDirectory is not null && Directory.Exists(workingDirectory)
                ? Path.GetFullPath(workingDirectory)
                : null;
            
            // Only reload if the directory actually changed
            if (_currentWorkingDirectory != normalizedPath)
            {
                _currentWorkingDirectory = normalizedPath;
                _repositories = null; // Force reload on next access
            }
        }
    }

    /// <summary>
    /// Gets the currently configured working directory, or null if using defaults.
    /// </summary>
    public string? CurrentWorkingDirectory
    {
        get
        {
            lock (_repositoryLock)
            {
                return _currentWorkingDirectory;
            }
        }
    }

    private IReadOnlyList<SourceRepository> GetRepositories()
    {
        lock (_repositoryLock)
        {
            _repositories ??= LoadConfiguredRepositories(_currentWorkingDirectory);
            return _repositories;
        }
    }

    public async Task<IReadOnlyList<string>> ListVersionsAsync(string packageId, CancellationToken ct)
    {
        var allVersions = new HashSet<NuGetVersion>();
        var tasks = new List<Task>();
        var repositories = GetRepositories();

        // Query all configured sources in parallel
        foreach (var repository in repositories)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var resource = await repository.GetResourceAsync<FindPackageByIdResource>(ct);
                    var versions = await resource.GetAllVersionsAsync(packageId, _cacheContext, NullLogger.Instance, ct);
                    
                    lock (allVersions)
                    {
                        foreach (var version in versions)
                        {
                            allVersions.Add(version);
                        }
                    }
                }
                catch
                {
                    // Ignore failures from individual sources
                    // SECURITY: Do not log or expose source URLs which may contain credentials
                }
            }, ct));
        }

        await Task.WhenAll(tasks);

        // If no versions found from configured sources, try fallback
        if (allVersions.Count == 0)
        {
            var resource = await _fallbackRepository.GetResourceAsync<FindPackageByIdResource>(ct);
            var versions = await resource.GetAllVersionsAsync(packageId, _cacheContext, NullLogger.Instance, ct);
            foreach (var version in versions)
            {
                allVersions.Add(version);
            }
        }

        return allVersions
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
        try
        {
            // Resolve version first (works the same regardless of CLI availability)
            var resolvedVersion = await ResolveVersionAsync(packageId, version, ct);

            // Try CLI-based restore first if available
            if (_cliRunner.IsSdkAvailable)
            {
                var cliResult = await TryLoadViaCliAsync(packageId, resolvedVersion, targetFramework, ct);
                if (cliResult is not null)
                    return cliResult;
            }

            // Fall back to API-based download
            return await LoadViaApiAsync(packageId, resolvedVersion, targetFramework, ct);
        }
        catch (Exception ex)
        {
            // SECURITY: Sanitize any error messages before exposing them
            throw new InvalidOperationException(SanitizeErrorMessage(ex.Message), ex.InnerException);
        }
    }

    /// <summary>
    /// Gets detailed metadata for a package from configured NuGet sources.
    /// </summary>
    public async Task<PackageMetadata?> GetPackageMetadataAsync(
        string packageId,
        string? version = null,
        CancellationToken ct = default)
    {
        try
        {
            var repositories = GetRepositories();
            IPackageSearchMetadata? metadata = null;

            // Try each repository until we find the package
            foreach (var repository in repositories)
            {
                try
                {
                    var resource = await repository.GetResourceAsync<PackageMetadataResource>(ct);
                    
                    if (!string.IsNullOrEmpty(version))
                    {
                        // Get specific version
                        var identity = new NuGet.Packaging.Core.PackageIdentity(packageId, NuGetVersion.Parse(version));
                        metadata = await resource.GetMetadataAsync(identity, _cacheContext, NullLogger.Instance, ct);
                    }
                    else
                    {
                        // Get latest version
                        var allMetadata = await resource.GetMetadataAsync(
                            packageId, 
                            includePrerelease: false, 
                            includeUnlisted: false, 
                            _cacheContext, 
                            NullLogger.Instance, 
                            ct);
                        
                        metadata = allMetadata
                            .OrderByDescending(m => m.Identity.Version)
                            .FirstOrDefault();
                    }

                    if (metadata is not null)
                        break;
                }
                catch
                {
                    // SECURITY: Do not log or expose source URLs which may contain credentials
                    continue;
                }
            }

            // Try fallback if not found
            if (metadata is null)
            {
                var resource = await _fallbackRepository.GetResourceAsync<PackageMetadataResource>(ct);
                
                if (!string.IsNullOrEmpty(version))
                {
                    var identity = new NuGet.Packaging.Core.PackageIdentity(packageId, NuGetVersion.Parse(version));
                    metadata = await resource.GetMetadataAsync(identity, _cacheContext, NullLogger.Instance, ct);
                }
                else
                {
                    var allMetadata = await resource.GetMetadataAsync(
                        packageId,
                        includePrerelease: false,
                        includeUnlisted: false,
                        _cacheContext,
                        NullLogger.Instance,
                        ct);

                    metadata = allMetadata
                        .OrderByDescending(m => m.Identity.Version)
                        .FirstOrDefault();
                }
            }

            if (metadata is null)
                return null;

            // Get deprecation metadata asynchronously
            PackageDeprecation? deprecation = null;
            try
            {
                var deprecationData = await metadata.GetDeprecationMetadataAsync();
                if (deprecationData is not null)
                {
                    deprecation = new PackageDeprecation(
                        deprecationData.Message,
                        deprecationData.Reasons?.ToList(),
                        deprecationData.AlternatePackage?.PackageId,
                        deprecationData.AlternatePackage?.Range?.ToString()
                    );
                }
            }
            catch
            {
                // Deprecation metadata might not be available
            }

            // Convert to our model
            return new PackageMetadata(
                metadata.Identity.Id,
                metadata.Identity.Version.ToNormalizedString(),
                metadata.Title,
                metadata.Description,
                metadata.Summary,
                metadata.Authors,
                metadata.Owners,
                metadata.Tags,
                metadata.ProjectUrl?.ToString(),
                metadata.LicenseUrl?.ToString(),
                metadata.LicenseMetadata?.License,
                metadata.IconUrl?.ToString(),
                metadata.ReadmeUrl?.ToString(),
                metadata.Published,
                metadata.DownloadCount,
                metadata.RequireLicenseAcceptance,
                metadata.IsListed,
                metadata.PrefixReserved,
                ConvertDependencies(metadata.DependencySets),
                ConvertVulnerabilities(metadata.Vulnerabilities),
                deprecation
            );
        }
        catch (Exception ex)
        {
            // SECURITY: Sanitize any error messages before exposing them
            throw new InvalidOperationException(SanitizeErrorMessage(ex.Message), ex.InnerException);
        }
    }

    /// <summary>
    /// Gets the list of currently configured NuGet sources based on working directory context.
    /// SECURITY: Source URLs are sanitized to remove any embedded credentials.
    /// </summary>
    public IReadOnlyList<NuGetSourceInfo> GetConfiguredSources()
    {
        var sources = new List<NuGetSourceInfo>();

        try
        {
            var settings = Settings.LoadDefaultSettings(root: _currentWorkingDirectory);
            var packageSourceProvider = new PackageSourceProvider(settings);
            var packageSources = packageSourceProvider.LoadPackageSources().ToList();

            foreach (var source in packageSources)
            {
                // SECURITY: Sanitize the source URL to remove any embedded credentials
                var sanitizedUrl = SanitizeSourceUrl(source.Source);
                
                sources.Add(new NuGetSourceInfo(
                    source.Name,
                    sanitizedUrl,
                    source.IsEnabled,
                    source.IsOfficial,
                    source.IsMachineWide,
                    source.ProtocolVersion > 0 ? source.ProtocolVersion.ToString() : null
                ));
            }
        }
        catch
        {
            // SECURITY: Do not expose error details which may contain credential information
        }

        // Always include the fallback source indicator
        var hasFallback = !sources.Any(s => 
            s.Url.Contains("api.nuget.org", StringComparison.OrdinalIgnoreCase));
        
        if (hasFallback || sources.Count == 0)
        {
            sources.Add(new NuGetSourceInfo(
                "nuget.org (fallback)",
                "https://api.nuget.org/v3/index.json",
                IsEnabled: true,
                IsOfficial: true,
                IsMachineWide: false,
                ProtocolVersion: "3"
            ));
        }

        return sources;
    }

    private static IReadOnlyList<Models.PackageDependencyGroup> ConvertDependencies(
        IEnumerable<NuGet.Packaging.PackageDependencyGroup>? dependencySets)
    {
        if (dependencySets is null)
            return [];

        return dependencySets.Select(group => new Models.PackageDependencyGroup(
            group.TargetFramework?.GetShortFolderName() ?? "any",
            group.Packages.Select(p => new Models.PackageDependency(
                p.Id,
                p.VersionRange?.ToString() ?? "*"
            )).ToList()
        )).ToList();
    }

    private static IReadOnlyList<Models.PackageVulnerability>? ConvertVulnerabilities(
        IEnumerable<PackageVulnerabilityMetadata>? vulnerabilities)
    {
        if (vulnerabilities is null || !vulnerabilities.Any())
            return null;

        return vulnerabilities.Select(v => new Models.PackageVulnerability(
            v.AdvisoryUrl?.ToString() ?? "",
            v.Severity.ToString()
        )).ToList();
    }

    /// <summary>
    /// Sanitizes a source URL to remove any embedded credentials.
    /// </summary>
    private static string SanitizeSourceUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        return CredentialInUrlPattern.Replace(url, "$1***@");
    }

    private async Task<PackageContent?> TryLoadViaCliAsync(
        string packageId,
        string version,
        string? targetFramework,
        CancellationToken ct)
    {
        // Check if already in global cache
        var packageDir = GetGlobalCachePackagePath(packageId, version);
        
        if (!Directory.Exists(packageDir))
        {
            // Try to restore via CLI
            var restored = await _cliRunner.RestorePackageAsync(packageId, version, ct);
            if (!restored || !Directory.Exists(packageDir))
                return null;
        }

        return BuildPackageContent(packageId, version, packageDir, targetFramework);
    }

    private async Task<PackageContent> LoadViaApiAsync(
        string packageId,
        string version,
        string? targetFramework,
        CancellationToken ct)
    {
        var packageDir = Path.Combine(FallbackCacheRoot, packageId.ToLowerInvariant(), version);

        // Check if already cached
        if (!Directory.Exists(packageDir) ||
            !Directory.GetFiles(packageDir, "*.dll", SearchOption.AllDirectories).Any())
        {
            await DownloadAndExtractAsync(packageId, version, packageDir, ct);
        }

        return BuildPackageContent(packageId, version, packageDir, targetFramework);
    }

    private PackageContent BuildPackageContent(
        string packageId,
        string version,
        string packageDir,
        string? targetFramework)
    {
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
            version,
            dllPath,
            xmlDocPath,
            pdbPath,
            selectedTfm,
            repositoryUrl,
            repositoryCommit
        );
    }

    private string GetGlobalCachePackagePath(string packageId, string version)
    {
        // Global cache structure: ~/.nuget/packages/{id.lowercase}/{version}/
        return Path.Combine(
            _cliRunner.GlobalPackagesPath,
            packageId.ToLowerInvariant(),
            version.ToLowerInvariant());
    }

    private async Task<string> ResolveVersionAsync(string packageId, string? version, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(version)) 
            return NuGetVersion.Parse(version).ToNormalizedString();

        // Get all versions from configured sources
        var versions = await ListVersionsAsync(packageId, ct);
        
        if (versions.Count == 0)
            throw new InvalidOperationException($"No versions found for package '{packageId}'");

        // Find latest stable, or latest prerelease if no stable exists
        var latest = versions
            .Select(NuGetVersion.Parse)
            .Where(v => !v.IsPrerelease)
            .OrderByDescending(v => v)
            .FirstOrDefault();

        if (latest is null)
        {
            latest = versions
                .Select(NuGetVersion.Parse)
                .OrderByDescending(v => v)
                .First();
        }

        return latest.ToNormalizedString();
    }

    private async Task DownloadAndExtractAsync(
        string packageId,
        string version,
        string packageDir,
        CancellationToken ct)
    {
        Directory.CreateDirectory(packageDir);

        var resource = await _fallbackRepository.GetResourceAsync<FindPackageByIdResource>(ct);
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

    private static IReadOnlyList<SourceRepository> LoadConfiguredRepositories(string? rootDirectory)
    {
        try
        {
            // Load settings from NuGet.config hierarchy starting from the specified root
            // This allows loading solution-specific configs when rootDirectory is set
            var settings = Settings.LoadDefaultSettings(root: rootDirectory);
            var packageSourceProvider = new PackageSourceProvider(settings);
            var sources = packageSourceProvider.LoadPackageSources()
                .Where(s => s.IsEnabled)
                .ToList();

            if (sources.Count == 0)
            {
                // No configured sources, use nuget.org as default
                sources.Add(new PackageSource("https://api.nuget.org/v3/index.json"));
            }

            return sources
                .Select(s => Repository.Factory.GetCoreV3(s))
                .ToList();
        }
        catch
        {
            // Fall back to nuget.org only
            // SECURITY: Do not log exception which may contain credential information
            var defaultSource = new PackageSource("https://api.nuget.org/v3/index.json");
            return [Repository.Factory.GetCoreV3(defaultSource)];
        }
    }

    /// <summary>
    /// Sanitizes error messages to remove any potential credential information.
    /// </summary>
    private static string SanitizeErrorMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        // Remove credentials from URLs (user:password@host or token@host patterns)
        var sanitized = CredentialInUrlPattern.Replace(message, "$1***@");
        
        // Also redact common credential-related terms that might appear in error messages
        sanitized = Regex.Replace(sanitized, 
            @"(api[_-]?key|password|secret|token|credential)['""]?\s*[:=]\s*['""]?[^'""&\s]+", 
            "$1=***", 
            RegexOptions.IgnoreCase);
        
        return sanitized;
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

        if (availableTfms.Count == 0) 
            throw new InvalidOperationException("No target framework folders found in lib/");

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
