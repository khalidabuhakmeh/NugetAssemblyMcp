namespace NuGetAssemblyMcp.Services.Models;

public record MemberDocumentation(
    string? Summary,
    string? Remarks,
    IReadOnlyDictionary<string, string> Parameters,
    string? Returns,
    IReadOnlyList<ExceptionDoc> Exceptions,
    IReadOnlyList<string> Examples,
    IReadOnlyList<string> SeeAlso,
    string? Value,
    IReadOnlyDictionary<string, string> TypeParameters
);

public record ExceptionDoc(
    string TypeName,
    string Description
);