using System.Xml.Linq;
using Mono.Cecil;
using NuGetAssemblyMcp.Services.Models;

namespace NuGetAssemblyMcp.Services;

public class XmlDocService
{
    private readonly Dictionary<string, XElement> _members = new(StringComparer.Ordinal);

    public XmlDocService(string? xmlDocPath)
    {
        if (string.IsNullOrEmpty(xmlDocPath) || !File.Exists(xmlDocPath))
            return;

        try
        {
            var doc = XDocument.Load(xmlDocPath);
            var membersElement = doc.Root?.Element("members");
            if (membersElement is null) return;

            foreach (var member in membersElement.Elements("member"))
            {
                var name = member.Attribute("name")?.Value;
                if (!string.IsNullOrEmpty(name)) _members[name] = member;
            }
        }
        catch
        {
            // If XML is malformed, treat as no docs available
        }
    }

    public MemberDocumentation? GetDocumentation(string memberId)
    {
        if (!_members.TryGetValue(memberId, out var element))
            return null;

        return ParseMemberElement(element);
    }

    public string? GetTypeDocumentation(string typeFullName)
    {
        var memberId = $"T:{typeFullName}";
        var doc = GetDocumentation(memberId);
        return doc?.Summary;
    }

    public string GenerateMemberId(TypeDefinition type)
    {
        return $"T:{FormatTypeName(type)}";
    }

    public string GenerateMemberId(MethodDefinition method)
    {
        var prefix = "M:";
        var typeName = FormatTypeName(method.DeclaringType);
        var methodName = method.Name;

        // Handle explicit interface implementations
        methodName = methodName.Replace('.', '#');

        var parameters = string.Empty;
        if (method.HasParameters)
            parameters = "(" + string.Join(",",
                method.Parameters.Select(p => FormatParameterType(p.ParameterType))) + ")";

        var genericSuffix = string.Empty;
        if (method.HasGenericParameters) genericSuffix = $"``{method.GenericParameters.Count}";

        // Handle conversion operators
        if (method.Name is "op_Explicit" or "op_Implicit")
            return
                $"{prefix}{typeName}.{methodName}{genericSuffix}{parameters}~{FormatParameterType(method.ReturnType)}";

        return $"{prefix}{typeName}.{methodName}{genericSuffix}{parameters}";
    }

    public string GenerateMemberId(PropertyDefinition property)
    {
        var typeName = FormatTypeName(property.DeclaringType);
        var parameters = string.Empty;
        if (property.HasParameters)
            parameters = "(" + string.Join(",",
                property.Parameters.Select(p => FormatParameterType(p.ParameterType))) + ")";

        return $"P:{typeName}.{property.Name}{parameters}";
    }

    public string GenerateMemberId(FieldDefinition field)
    {
        var typeName = FormatTypeName(field.DeclaringType);
        return $"F:{typeName}.{field.Name}";
    }

    public string GenerateMemberId(EventDefinition evt)
    {
        var typeName = FormatTypeName(evt.DeclaringType);
        return $"E:{typeName}.{evt.Name}";
    }

    private static string FormatTypeName(TypeDefinition type)
    {
        var name = type.FullName.Replace('/', '.');

        // Handle generic types: List`1 stays as List`1
        return name;
    }

    private static string FormatParameterType(TypeReference typeRef)
    {
        if (typeRef is GenericParameter gp)
            // Method generic parameter: ``0, ``1, etc.
            // Type generic parameter: `0, `1, etc.
            return gp.Owner is MethodReference
                ? $"``{gp.Position}"
                : $"`{gp.Position}";

        if (typeRef is ArrayType arrayType)
        {
            var elementType = FormatParameterType(arrayType.ElementType);
            if (arrayType.Rank == 1)
                return $"{elementType}[]";
            return $"{elementType}[{new string(',', arrayType.Rank - 1)}]";
        }

        if (typeRef is ByReferenceType byRef) return $"{FormatParameterType(byRef.ElementType)}@";

        if (typeRef is PointerType pointer) return $"{FormatParameterType(pointer.ElementType)}*";

        if (typeRef is GenericInstanceType genericInstance)
        {
            var baseName = genericInstance.ElementType.FullName;
            var args = string.Join(",",
                genericInstance.GenericArguments.Select(FormatParameterType));
            return $"{baseName}{{{args}}}";
        }

        return typeRef.FullName.Replace('/', '.');
    }

    private MemberDocumentation ParseMemberElement(XElement element)
    {
        var summary = GetInnerText(element.Element("summary"));
        var remarks = GetInnerText(element.Element("remarks"));
        var returns = GetInnerText(element.Element("returns"));
        var value = GetInnerText(element.Element("value"));

        var parameters = new Dictionary<string, string>();
        foreach (var param in element.Elements("param"))
        {
            var name = param.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(name)) parameters[name] = GetInnerText(param) ?? string.Empty;
        }

        var typeParameters = new Dictionary<string, string>();
        foreach (var typeParam in element.Elements("typeparam"))
        {
            var name = typeParam.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(name)) typeParameters[name] = GetInnerText(typeParam) ?? string.Empty;
        }

        var exceptions = element.Elements("exception")
            .Select(e => new ExceptionDoc(
                ExtractCref(e.Attribute("cref")?.Value),
                GetInnerText(e) ?? string.Empty))
            .ToList();

        var examples = element.Elements("example")
            .Select(e => GetInnerText(e) ?? string.Empty)
            .Where(e => !string.IsNullOrEmpty(e))
            .ToList();

        var seeAlso = element.Elements("seealso")
            .Select(e => ExtractCref(e.Attribute("cref")?.Value))
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        return new MemberDocumentation(
            summary,
            remarks,
            parameters,
            returns,
            exceptions,
            examples,
            seeAlso,
            value,
            typeParameters
        );
    }

    private static string? GetInnerText(XElement? element)
    {
        if (element is null)
            return null;

        // Process XML nodes to plain text, resolving <see cref="..."/> etc.
        var text = ProcessXmlNodes(element);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string ProcessXmlNodes(XElement element)
    {
        var parts = new List<string>();

        foreach (var node in element.Nodes())
            switch (node)
            {
                case XText textNode:
                    parts.Add(textNode.Value);
                    break;
                case XElement childElement:
                    switch (childElement.Name.LocalName)
                    {
                        case "see" or "seealso":
                            var cref = childElement.Attribute("cref")?.Value;
                            var langword = childElement.Attribute("langword")?.Value;
                            if (!string.IsNullOrEmpty(cref))
                                parts.Add(ExtractCref(cref));
                            else if (!string.IsNullOrEmpty(langword))
                                parts.Add(langword);
                            else
                                parts.Add(childElement.Value);
                            break;
                        case "paramref":
                            parts.Add(childElement.Attribute("name")?.Value ?? string.Empty);
                            break;
                        case "typeparamref":
                            parts.Add(childElement.Attribute("name")?.Value ?? string.Empty);
                            break;
                        case "c":
                            parts.Add($"`{childElement.Value}`");
                            break;
                        case "code":
                            parts.Add($"\n```\n{childElement.Value}\n```\n");
                            break;
                        case "para":
                            parts.Add($"\n{ProcessXmlNodes(childElement)}\n");
                            break;
                        default:
                            parts.Add(ProcessXmlNodes(childElement));
                            break;
                    }

                    break;
            }

        return string.Join(string.Empty, parts);
    }

    private static string ExtractCref(string? cref)
    {
        if (string.IsNullOrEmpty(cref))
            return string.Empty;

        // Strip the prefix (T:, M:, P:, F:, E:, N:, !:)
        if (cref.Length > 2 && cref[1] == ':') cref = cref[2..];

        // Take the last segment for readability
        var lastDot = cref.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < cref.Length - 1)
        {
            // Check if what follows is a method with parameters
            var afterDot = cref[(lastDot + 1)..];
            var parenIdx = afterDot.IndexOf('(');
            if (parenIdx >= 0)
                return afterDot[..parenIdx];
            return afterDot;
        }

        return cref;
    }
}