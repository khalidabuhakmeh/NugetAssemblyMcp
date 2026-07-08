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