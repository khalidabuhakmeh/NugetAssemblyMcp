namespace NuGetAssemblyMcp.Services.Models;

public record SourceLinkInfo(
    string? RepositoryUrl,
    string? Commit,
    IReadOnlyDictionary<string, string> DocumentMappings,
    string? RepositoryBrowseUrl
);