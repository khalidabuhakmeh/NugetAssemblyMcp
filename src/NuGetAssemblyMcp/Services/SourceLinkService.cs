using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using NuGetAssemblyMcp.Services.Models;

namespace NuGetAssemblyMcp.Services;

public class SourceLinkService
{
    // Well-known GUID for SourceLink custom debug information
    private static readonly Guid SourceLinkGuid = new("CC110190-B3E0-4B0C-B22A-6E49B6DA1E76");

    public SourceLinkInfo? GetSourceLinkInfo(string? pdbPath, string? repositoryUrl, string? repositoryCommit)
    {
        var documentMappings = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(pdbPath) && File.Exists(pdbPath)) documentMappings = ReadSourceLinkFromPdb(pdbPath);

        // If no PDB data and no repo info, nothing to return
        if (documentMappings.Count == 0 && string.IsNullOrEmpty(repositoryUrl))
            return null;

        // Try to infer commit from SourceLink URL if not provided
        var commit = repositoryCommit;
        if (string.IsNullOrEmpty(commit) && documentMappings.Count > 0)
            commit = TryExtractCommitFromMapping(documentMappings.Values.FirstOrDefault());

        var browseUrl = BuildRepositoryBrowseUrl(repositoryUrl, commit);

        return new SourceLinkInfo(
            repositoryUrl,
            commit,
            documentMappings,
            browseUrl
        );
    }

    public string? GetSourceUrl(SourceLinkInfo info, string documentPath)
    {
        // Normalize path separators
        var normalizedPath = documentPath.Replace('\\', '/');

        foreach (var (pattern, urlTemplate) in info.DocumentMappings)
        {
            var normalizedPattern = pattern.Replace('\\', '/');
            var match = MatchPattern(normalizedPattern, normalizedPath);
            if (match is not null) return urlTemplate.Replace("*", match);
        }

        return null;
    }

    public string? GetRepositoryBrowseUrl(SourceLinkInfo info)
    {
        return info.RepositoryBrowseUrl;
    }

    private static Dictionary<string, string> ReadSourceLinkFromPdb(string pdbPath)
    {
        try
        {
            using var stream = File.OpenRead(pdbPath);
            return ReadSourceLinkFromStream(stream);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static Dictionary<string, string> ReadSourceLinkFromStream(Stream stream)
    {
        try
        {
            // Check if this is a PE file (DLL with embedded PDB) or a standalone PDB
            stream.Position = 0;
            var firstBytes = new byte[2];
            if (stream.Read(firstBytes, 0, 2) == 2 && firstBytes[0] == 'M' && firstBytes[1] == 'Z')
            {
                // This is a PE file — look for embedded PDB
                stream.Position = 0;
                return ReadSourceLinkFromPE(stream);
            }

            // Try as portable PDB
            stream.Position = 0;
            using var provider = MetadataReaderProvider.FromPortablePdbStream(stream,
                MetadataStreamOptions.LeaveOpen);
            var reader = provider.GetMetadataReader();
            return ExtractSourceLinkFromReader(reader);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static Dictionary<string, string> ReadSourceLinkFromPE(Stream stream)
    {
        try
        {
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);

            // Check for embedded PDB
            foreach (var entry in peReader.ReadDebugDirectory())
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    using var embeddedProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                    var reader = embeddedProvider.GetMetadataReader();
                    return ExtractSourceLinkFromReader(reader);
                }

            return new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static Dictionary<string, string> ExtractSourceLinkFromReader(MetadataReader reader)
    {
        foreach (var handle in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var info = reader.GetCustomDebugInformation(handle);
            var guid = reader.GetGuid(info.Kind);

            if (guid == SourceLinkGuid)
            {
                var blob = reader.GetBlobBytes(info.Value);
                var json = Encoding.UTF8.GetString(blob);
                return ParseSourceLinkJson(json);
            }
        }

        return new Dictionary<string, string>();
    }

    private static Dictionary<string, string> ParseSourceLinkJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<string, string>();

            if (doc.RootElement.TryGetProperty("documents", out var documents))
                foreach (var prop in documents.EnumerateObject())
                    result[prop.Name] = prop.Value.GetString() ?? string.Empty;

            return result;
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static string? MatchPattern(string pattern, string path)
    {
        // Pattern uses * as a wildcard for a path prefix
        // e.g., "/_/*" matches "/_/src/MyFile.cs" and returns "src/MyFile.cs"
        var asteriskIndex = pattern.IndexOf('*');
        if (asteriskIndex < 0)
            // Exact match
            return pattern.Equals(path, StringComparison.OrdinalIgnoreCase) ? string.Empty : null;

        var prefix = pattern[..asteriskIndex];
        var suffix = pattern[(asteriskIndex + 1)..];

        var pathMatchesPrefix = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        var pathMatchesSuffix = string.IsNullOrEmpty(suffix) ||
                                path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

        if (pathMatchesPrefix && pathMatchesSuffix)
        {
            var matchedPortion = path[prefix.Length..];
            if (!string.IsNullOrEmpty(suffix)) matchedPortion = matchedPortion[..^suffix.Length];

            return matchedPortion;
        }

        return null;
    }

    private static string? TryExtractCommitFromMapping(string? urlTemplate)
    {
        if (string.IsNullOrEmpty(urlTemplate))
            return null;

        // Try to extract commit hash from common URL patterns
        // e.g., https://raw.githubusercontent.com/org/repo/abc123def/*
        // e.g., https://raw.githubusercontent.com/org/repo/abc123def456789/*
        var parts = urlTemplate.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Replace("*", string.Empty);
            // A commit hash is typically 40 chars hex, but could be abbreviated
            if (part.Length >= 7 && part.Length <= 40 &&
                part.All(c => char.IsAsciiHexDigit(c)))
                return part;
        }

        return null;
    }

    private static string? BuildRepositoryBrowseUrl(string? repositoryUrl, string? commit)
    {
        if (string.IsNullOrEmpty(repositoryUrl))
            return null;

        // Normalize the repo URL
        var url = repositoryUrl.TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];

        if (!string.IsNullOrEmpty(commit))
        {
            // GitHub/GitLab/Azure DevOps tree URL
            if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("gitlab.com", StringComparison.OrdinalIgnoreCase))
                return $"{url}/tree/{commit}";

            if (url.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase)) return $"{url}?version=GC{commit}";
        }

        return url;
    }
}