using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using NuGetAssemblyMcp.Services;
using NuGetAssemblyMcp.Services.Models;

namespace NuGetAssemblyMcp.Tools;

[McpServerToolType]
public class AssemblyTools(
    NuGetPackageService packageService,
    AssemblyInspectionService assemblyService,
    SourceLinkService sourceLinkService)
{
    private async Task<PackageContent> EnsurePackageLoadedAsync(
        string packageId, string? version, string? targetFramework, CancellationToken ct)
    {
        return await packageService.LoadPackageAsync(packageId, version, targetFramework, ct);
    }

    [McpServerTool(Name = "list_namespaces")]
    [Description("Lists all namespaces found in the primary assembly of a NuGet package.")]
    public async Task<string> ListNamespaces(
        [Description("The NuGet package ID")] string packageId,
        [Description("The package version. Defaults to latest if omitted.")]
        string? version = null,
        [Description("The target framework moniker (e.g. 'net8.0'). Auto-selected if omitted.")]
        string? targetFramework = null,
        CancellationToken ct = default)
    {
        var content = await EnsurePackageLoadedAsync(packageId, version, targetFramework, ct);
        var namespaces = assemblyService.GetNamespaces(content.DllPath);

        var sb = new StringBuilder();
        sb.AppendLine($"## Namespaces in {content.PackageId} v{content.Version} ({content.TargetFramework})");
        sb.AppendLine();
        foreach (var ns in namespaces) sb.AppendLine($"- {ns}");

        return sb.ToString();
    }

    [McpServerTool(Name = "list_types")]
    [Description(
        "Lists types (classes, interfaces, structs, enums, delegates) in a NuGet package assembly, optionally filtered by namespace.")]
    public async Task<string> ListTypes(
        [Description("The NuGet package ID")] string packageId,
        [Description("Optional namespace filter. If provided, only types in this namespace are returned.")]
        string? ns = null,
        [Description("The package version. Defaults to latest if omitted.")]
        string? version = null,
        [Description("The target framework moniker (e.g. 'net8.0'). Auto-selected if omitted.")]
        string? targetFramework = null,
        CancellationToken ct = default)
    {
        var content = await EnsurePackageLoadedAsync(packageId, version, targetFramework, ct);
        var types = assemblyService.GetTypes(content.DllPath, ns);

        var sb = new StringBuilder();
        sb.AppendLine($"## Types in {content.PackageId} v{content.Version}");
        if (ns is not null)
            sb.AppendLine($"**Namespace filter:** {ns}");
        sb.AppendLine();

        if (types.Count == 0)
            sb.AppendLine("_No types found matching the filter._");
        else
            foreach (var type in types)
            {
                var visibility = type.IsPublic ? "public" : "internal";
                sb.AppendLine($"- `{type.FullName}` ({type.Kind}, {visibility})");
            }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_type_info")]
    [Description(
        "Returns detailed information about a specific type including its members, XML documentation, " +
        "and source link URL. Provides a complete overview useful for understanding a type's API surface.")]
    public async Task<string> GetTypeInfo(
        [Description("The NuGet package ID")] string packageId,
        [Description("The fully-qualified type name (e.g. 'Newtonsoft.Json.JsonConvert')")]
        string typeFullName,
        [Description("The package version. Defaults to latest if omitted.")]
        string? version = null,
        [Description("The target framework moniker (e.g. 'net8.0'). Auto-selected if omitted.")]
        string? targetFramework = null,
        CancellationToken ct = default)
    {
        var content = await EnsurePackageLoadedAsync(packageId, version, targetFramework, ct);

        TypeDetail typeDetail;
        try
        {
            typeDetail = assemblyService.GetTypeDetail(content.DllPath, typeFullName);
        }
        catch (InvalidOperationException)
        {
            return $"Type `{typeFullName}` not found in {content.PackageId} v{content.Version}.";
        }

        // Load XML documentation if available
        var xmlDoc = content.XmlDocPath is not null ? new XmlDocService(content.XmlDocPath) : null;

        // Load source link info if available
        var sourceLinkInfo = sourceLinkService.GetSourceLinkInfo(
            content.PdbPath, content.RepositoryUrl, content.RepositoryCommit);

        var sb = new StringBuilder();
        sb.AppendLine($"## Type: {typeDetail.FullName}");
        sb.AppendLine($"**Namespace:** {typeDetail.Namespace}");
        sb.AppendLine($"**Kind:** {typeDetail.Kind}");
        sb.AppendLine($"**Visibility:** {(typeDetail.IsPublic ? "public" : "internal")}");

        if (typeDetail.BaseTypeName is not null)
            sb.AppendLine($"**Base Type:** {typeDetail.BaseTypeName}");

        if (typeDetail.InterfaceNames.Count > 0)
            sb.AppendLine($"**Implements:** {string.Join(", ", typeDetail.InterfaceNames)}");

        if (typeDetail.GenericParameters.Count > 0)
            sb.AppendLine($"**Generic Parameters:** <{string.Join(", ", typeDetail.GenericParameters)}>");

        // XML documentation summary for the type
        if (xmlDoc is not null)
        {
            var typeSummary = xmlDoc.GetTypeDocumentation(typeFullName);
            if (typeSummary is not null)
            {
                sb.AppendLine();
                sb.AppendLine("### Summary");
                sb.AppendLine(typeSummary);
            }
        }

        // Source link
        if (sourceLinkInfo?.RepositoryBrowseUrl is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"**Repository:** {sourceLinkInfo.RepositoryBrowseUrl}");
        }

        // Group members by kind
        var constructors = typeDetail.Members.Where(m => m.MemberKind == MemberKind.Constructor).ToList();
        var properties = typeDetail.Members.Where(m => m.MemberKind == MemberKind.Property).ToList();
        var methods = typeDetail.Members.Where(m => m.MemberKind == MemberKind.Method).ToList();
        var events = typeDetail.Members.Where(m => m.MemberKind == MemberKind.Event).ToList();
        var fields = typeDetail.Members.Where(m => m.MemberKind == MemberKind.Field).ToList();

        if (constructors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Constructors");
            foreach (var ctor in constructors.Where(c => c.IsPublic))
            {
                var paramList = FormatParameters(ctor.Parameters);
                sb.AppendLine($"- `{ctor.Name}({paramList})`");
                AppendMemberDoc(sb, xmlDoc, typeFullName, ctor);
            }
        }

        if (properties.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Properties");
            foreach (var prop in properties.Where(p => p.IsPublic))
            {
                sb.AppendLine($"- `{prop.Name}` ({prop.ReturnType ?? "void"}) — {prop.Accessibility}");
                AppendMemberDoc(sb, xmlDoc, typeFullName, prop);
            }
        }

        if (methods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Methods");
            foreach (var method in methods.Where(m => m.IsPublic))
            {
                var paramList = FormatParameters(method.Parameters);
                sb.AppendLine(
                    $"- `{method.Name}({paramList})` → {method.ReturnType ?? "void"}{(method.IsStatic ? " [static]" : "")}");
                AppendMemberDoc(sb, xmlDoc, typeFullName, method);
            }
        }

        if (events.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Events");
            foreach (var evt in events.Where(e => e.IsPublic))
            {
                sb.AppendLine($"- `{evt.Name}` ({evt.ReturnType ?? "void"})");
                AppendMemberDoc(sb, xmlDoc, typeFullName, evt);
            }
        }

        if (fields.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Fields");
            foreach (var field in fields.Where(f => f.IsPublic))
            {
                sb.AppendLine(
                    $"- `{field.Name}` ({field.ReturnType ?? "void"}) — {field.Accessibility}{(field.IsStatic ? " [static]" : "")}");
                AppendMemberDoc(sb, xmlDoc, typeFullName, field);
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_member_info")]
    [Description(
        "Returns detailed information about a specific member (method, property, field, event) " +
        "of a type, including parameters, XML documentation, and source link.")]
    public async Task<string> GetMemberInfo(
        [Description("The NuGet package ID")] string packageId,
        [Description("The fully-qualified type name (e.g. 'Newtonsoft.Json.JsonConvert')")]
        string typeFullName,
        [Description("The member name (e.g. 'SerializeObject')")]
        string memberName,
        [Description("The package version. Defaults to latest if omitted.")]
        string? version = null,
        [Description("The target framework moniker (e.g. 'net8.0'). Auto-selected if omitted.")]
        string? targetFramework = null,
        CancellationToken ct = default)
    {
        var content = await EnsurePackageLoadedAsync(packageId, version, targetFramework, ct);

        MemberDetail memberDetail;
        try
        {
            memberDetail = assemblyService.GetMemberDetail(content.DllPath, typeFullName, memberName);
        }
        catch (InvalidOperationException)
        {
            return
                $"Member `{memberName}` not found on type `{typeFullName}` in {content.PackageId} v{content.Version}.";
        }

        // Load XML documentation if available
        var xmlDoc = content.XmlDocPath is not null ? new XmlDocService(content.XmlDocPath) : null;

        // Load source link info if available
        var sourceLinkInfo = sourceLinkService.GetSourceLinkInfo(
            content.PdbPath, content.RepositoryUrl, content.RepositoryCommit);

        var sb = new StringBuilder();
        sb.AppendLine($"## {typeFullName}.{memberName}");
        sb.AppendLine();
        sb.AppendLine($"**Kind:** {memberDetail.MemberKind}");
        sb.AppendLine($"**Visibility:** {memberDetail.Accessibility}");

        if (memberDetail.ReturnType is not null)
            sb.AppendLine($"**Returns:** {memberDetail.ReturnType}");

        if (memberDetail.IsStatic)
            sb.AppendLine("**Static:** yes");

        if (memberDetail.GenericParameters.Count > 0)
            sb.AppendLine($"**Generic Parameters:** <{string.Join(", ", memberDetail.GenericParameters)}>");

        if (memberDetail.Parameters.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Parameters");
            foreach (var param in memberDetail.Parameters)
            {
                var defaultStr = param.IsOptional ? $" = {param.DefaultValue ?? "default"}" : "";
                sb.AppendLine($"- `{param.Name}` ({param.TypeName}){defaultStr}");
            }
        }

        if (memberDetail.Attributes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Attributes");
            foreach (var attr in memberDetail.Attributes) sb.AppendLine($"- [{attr}]");
        }

        // XML documentation
        if (xmlDoc is not null)
        {
            // Build a simple member ID for lookup: "M:Namespace.Type.Member" or "P:" etc.
            var prefix = memberDetail.MemberKind switch
            {
                MemberKind.Method or MemberKind.Constructor => "M",
                MemberKind.Property => "P",
                MemberKind.Field => "F",
                MemberKind.Event => "E",
                _ => "M"
            };
            var memberId = $"{prefix}:{typeFullName}.{memberName}";
            var doc = xmlDoc.GetDocumentation(memberId);

            if (doc is not null)
            {
                sb.AppendLine();
                sb.AppendLine("### Documentation");
                if (doc.Summary is not null)
                    sb.AppendLine($"**Summary:** {doc.Summary}");
                if (doc.Remarks is not null)
                    sb.AppendLine($"**Remarks:** {doc.Remarks}");
                if (doc.Returns is not null)
                    sb.AppendLine($"**Returns:** {doc.Returns}");

                if (doc.Parameters.Count > 0)
                {
                    sb.AppendLine("**Parameter docs:**");
                    foreach (var (paramName, paramDoc) in doc.Parameters)
                        sb.AppendLine($"  - `{paramName}`: {paramDoc}");
                }

                if (doc.Exceptions.Count > 0)
                {
                    sb.AppendLine("**Exceptions:**");
                    foreach (var ex in doc.Exceptions) sb.AppendLine($"  - `{ex.TypeName}`: {ex.Description}");
                }
            }
        }

        // Source link
        if (sourceLinkInfo?.RepositoryBrowseUrl is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"**Repository:** {sourceLinkInfo.RepositoryBrowseUrl}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "search_types")]
    [Description("Searches for types in a NuGet package assembly by name pattern (supports regex).")]
    public async Task<string> SearchTypes(
        [Description("The NuGet package ID")] string packageId,
        [Description("Regex pattern to match against fully-qualified type names")]
        string pattern,
        [Description("The package version. Defaults to latest if omitted.")]
        string? version = null,
        [Description("The target framework moniker (e.g. 'net8.0'). Auto-selected if omitted.")]
        string? targetFramework = null,
        CancellationToken ct = default)
    {
        var content = await EnsurePackageLoadedAsync(packageId, version, targetFramework, ct);
        var results = assemblyService.SearchTypes(content.DllPath, pattern);

        var sb = new StringBuilder();
        sb.AppendLine($"## Search results for `{pattern}` in {content.PackageId} v{content.Version}");
        sb.AppendLine();

        if (results.Count == 0)
        {
            sb.AppendLine("_No types found matching the pattern._");
        }
        else
        {
            sb.AppendLine($"Found {results.Count} matching type(s):");
            sb.AppendLine();
            foreach (var type in results)
            {
                var visibility = type.IsPublic ? "public" : "internal";
                sb.AppendLine($"- `{type.FullName}` ({type.Kind}, {visibility})");
            }
        }

        return sb.ToString();
    }

    private static string FormatParameters(IReadOnlyList<ParameterInfo> parameters)
    {
        if (parameters.Count == 0)
            return "";

        return string.Join(", ", parameters.Select(p =>
        {
            var defaultStr = p.IsOptional ? $" = {p.DefaultValue ?? "default"}" : "";
            return $"{p.TypeName} {p.Name}{defaultStr}";
        }));
    }

    private static void AppendMemberDoc(StringBuilder sb, XmlDocService? xmlDoc, string typeFullName,
        MemberSummary member)
    {
        if (xmlDoc is null) return;

        // Build member ID for XML doc lookup
        var prefix = member.MemberKind switch
        {
            MemberKind.Method or MemberKind.Constructor => "M",
            MemberKind.Property => "P",
            MemberKind.Field => "F",
            MemberKind.Event => "E",
            _ => "M"
        };

        // For methods/constructors with parameters, we need the full signature
        // For a simple lookup, try without parameters first
        var memberId = $"{prefix}:{typeFullName}.{member.Name}";
        var doc = xmlDoc.GetDocumentation(memberId);

        if (doc?.Summary is not null) sb.AppendLine($"  > {doc.Summary}");
    }
}