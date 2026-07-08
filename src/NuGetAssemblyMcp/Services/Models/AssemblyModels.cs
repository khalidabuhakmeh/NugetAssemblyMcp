namespace NuGetAssemblyMcp.Services.Models;

public enum TypeKind
{
    Class,
    Interface,
    Enum,
    Struct,
    Delegate
}

public enum MemberKind
{
    Method,
    Property,
    Field,
    Event,
    Constructor
}

public record AssemblyInfo(
    string Name,
    string Version,
    string? TargetFramework,
    string? Culture,
    string? PublicKeyToken
);

public record TypeSummary(
    string FullName,
    string Name,
    string Namespace,
    TypeKind Kind,
    bool IsPublic,
    string? BaseTypeName,
    IReadOnlyList<string> InterfaceNames
);

public record TypeDetail(
    string FullName,
    string Name,
    string Namespace,
    TypeKind Kind,
    bool IsPublic,
    string? BaseTypeName,
    IReadOnlyList<string> InterfaceNames,
    IReadOnlyList<MemberSummary> Members,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<string> Attributes
);

public record MemberSummary(
    string Name,
    MemberKind MemberKind,
    string? ReturnType,
    IReadOnlyList<ParameterInfo> Parameters,
    bool IsStatic,
    bool IsPublic,
    string Accessibility
);

public record MemberDetail(
    string Name,
    MemberKind MemberKind,
    string? ReturnType,
    IReadOnlyList<ParameterInfo> Parameters,
    bool IsStatic,
    bool IsPublic,
    string Accessibility,
    IReadOnlyList<string> Attributes,
    IReadOnlyList<string> GenericParameters
);

public record ParameterInfo(
    string Name,
    string TypeName,
    bool IsOptional,
    string? DefaultValue
);