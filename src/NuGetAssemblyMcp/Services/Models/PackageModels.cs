namespace NuGetAssemblyMcp.Services.Models;

public record PackageContent(
    string PackageId,
    string Version,
    string DllPath,
    string? XmlDocPath,
    string? PdbPath,
    string TargetFramework,
    string? RepositoryUrl,
    string? RepositoryCommit
);

/// <summary>
/// Package metadata as found on NuGet.org or in nuspec files.
/// </summary>
public record PackageMetadata(
    string PackageId,
    string Version,
    string? Title,
    string? Description,
    string? Summary,
    string? Authors,
    string? Owners,
    string? Tags,
    string? ProjectUrl,
    string? LicenseUrl,
    string? LicenseExpression,
    string? IconUrl,
    string? ReadmeUrl,
    DateTimeOffset? Published,
    long? DownloadCount,
    bool RequireLicenseAcceptance,
    bool IsListed,
    bool PrefixReserved,
    IReadOnlyList<PackageDependencyGroup> Dependencies,
    IReadOnlyList<PackageVulnerability>? Vulnerabilities,
    PackageDeprecation? Deprecation
);

/// <summary>
/// A group of dependencies for a specific target framework.
/// </summary>
public record PackageDependencyGroup(
    string TargetFramework,
    IReadOnlyList<PackageDependency> Dependencies
);

/// <summary>
/// A package dependency with version range.
/// </summary>
public record PackageDependency(
    string PackageId,
    string VersionRange
);

/// <summary>
/// Vulnerability information for a package.
/// </summary>
public record PackageVulnerability(
    string AdvisoryUrl,
    string Severity
);

/// <summary>
/// Deprecation information for a package.
/// </summary>
public record PackageDeprecation(
    string? Message,
    IReadOnlyList<string>? Reasons,
    string? AlternatePackageId,
    string? AlternateVersionRange
);

/// <summary>
/// Information about a configured NuGet source.
/// </summary>
public record NuGetSourceInfo(
    string Name,
    string Url,
    bool IsEnabled,
    bool IsOfficial,
    bool IsMachineWide,
    string? ProtocolVersion
)
{
    // SECURITY: Never expose credentials - these are sanitized URLs only
};