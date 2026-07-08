using System.Text.RegularExpressions;
using Mono.Cecil;
using NuGetAssemblyMcp.Services.Models;
using ParameterInfo = NuGetAssemblyMcp.Services.Models.ParameterInfo;

namespace NuGetAssemblyMcp.Services;

public class AssemblyInspectionService
{
    public AssemblyInfo GetAssemblyInfo(string dllPath)
    {
        using var assembly = ReadAssembly(dllPath);
        var name = assembly.Name;

        var tfmAttr = assembly.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.FullName ==
                                 "System.Runtime.Versioning.TargetFrameworkAttribute");
        var targetFramework = tfmAttr?.ConstructorArguments.FirstOrDefault().Value?.ToString();

        var publicKeyToken = name.PublicKeyToken is { Length: > 0 }
            ? BitConverter.ToString(name.PublicKeyToken).Replace("-", "").ToLowerInvariant()
            : null;

        return new AssemblyInfo(
            name.Name,
            name.Version.ToString(),
            targetFramework,
            string.IsNullOrEmpty(name.Culture) ? null : name.Culture,
            publicKeyToken
        );
    }

    public IReadOnlyList<string> GetNamespaces(string dllPath)
    {
        using var assembly = ReadAssembly(dllPath);
        return assembly.MainModule.Types
            .Where(t => t.IsPublic)
            .Select(t => t.Namespace)
            .Where(ns => !string.IsNullOrEmpty(ns))
            .Distinct()
            .OrderBy(ns => ns)
            .ToList();
    }

    public IReadOnlyList<TypeSummary> GetTypes(string dllPath, string? namespaceFilter = null)
    {
        using var assembly = ReadAssembly(dllPath);
        var types = assembly.MainModule.Types
            .Where(t => t.IsPublic || t.IsNestedPublic);

        if (!string.IsNullOrEmpty(namespaceFilter))
            types = types.Where(t =>
                t.Namespace?.Equals(namespaceFilter, StringComparison.OrdinalIgnoreCase) == true);

        return types
            .Select(ToTypeSummary)
            .OrderBy(t => t.FullName)
            .ToList();
    }

    public TypeDetail GetTypeDetail(string dllPath, string typeFullName)
    {
        using var assembly = ReadAssembly(dllPath);
        var type = FindType(assembly, typeFullName)
                   ?? throw new InvalidOperationException($"Type '{typeFullName}' not found");

        var summary = ToTypeSummary(type);
        var members = GetMembers(type);
        var genericParams = type.GenericParameters.Select(p => p.Name).ToList();
        var attributes = type.CustomAttributes
            .Select(a => a.AttributeType.FullName)
            .ToList();

        return new TypeDetail(
            summary.FullName,
            summary.Name,
            summary.Namespace,
            summary.Kind,
            summary.IsPublic,
            summary.BaseTypeName,
            summary.InterfaceNames,
            members,
            genericParams,
            attributes
        );
    }

    public MemberDetail GetMemberDetail(string dllPath, string typeFullName, string memberName)
    {
        using var assembly = ReadAssembly(dllPath);
        var type = FindType(assembly, typeFullName)
                   ?? throw new InvalidOperationException($"Type '{typeFullName}' not found");

        // Search methods
        var method = type.Methods.FirstOrDefault(m => m.Name == memberName);
        if (method is not null) return ToMemberDetail(method);

        // Search properties
        var property = type.Properties.FirstOrDefault(p => p.Name == memberName);
        if (property is not null) return ToMemberDetail(property);

        // Search fields
        var field = type.Fields.FirstOrDefault(f => f.Name == memberName);
        if (field is not null) return ToMemberDetail(field);

        // Search events
        var evt = type.Events.FirstOrDefault(e => e.Name == memberName);
        if (evt is not null) return ToMemberDetail(evt);

        throw new InvalidOperationException(
            $"Member '{memberName}' not found on type '{typeFullName}'");
    }

    public IReadOnlyList<TypeSummary> SearchTypes(string dllPath, string pattern)
    {
        using var assembly = ReadAssembly(dllPath);

        Regex? regex = null;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        catch
        {
            // Fall back to contains search if regex is invalid
        }

        return assembly.MainModule.Types
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Where(t =>
            {
                if (regex is not null)
                    return regex.IsMatch(t.FullName);
                return t.FullName.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                       || t.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            })
            .Select(ToTypeSummary)
            .OrderBy(t => t.FullName)
            .ToList();
    }

    private static AssemblyDefinition ReadAssembly(string dllPath)
    {
        var parameters = new ReaderParameters
        {
            ReadingMode = ReadingMode.Deferred
        };

        // Try reading with symbols first
        try
        {
            parameters.ReadSymbols = true;
            return AssemblyDefinition.ReadAssembly(dllPath, parameters);
        }
        catch
        {
            parameters.ReadSymbols = false;
            return AssemblyDefinition.ReadAssembly(dllPath, parameters);
        }
    }

    private static TypeDefinition? FindType(AssemblyDefinition assembly, string typeFullName)
    {
        // Try direct lookup
        var type = assembly.MainModule.GetType(typeFullName);
        if (type is not null)
            return type;

        // Try nested types and case-insensitive search
        return assembly.MainModule.Types
            .SelectMany(t => GetAllNestedTypes(t))
            .Concat(assembly.MainModule.Types)
            .FirstOrDefault(t => t.FullName.Equals(typeFullName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<TypeDefinition> GetAllNestedTypes(TypeDefinition type)
    {
        foreach (var nested in type.NestedTypes)
        {
            yield return nested;
            foreach (var deepNested in GetAllNestedTypes(nested)) yield return deepNested;
        }
    }

    private static TypeSummary ToTypeSummary(TypeDefinition type)
    {
        return new TypeSummary(
            type.FullName,
            type.Name,
            type.Namespace ?? string.Empty,
            GetTypeKind(type),
            type.IsPublic || type.IsNestedPublic,
            type.BaseType?.FullName,
            type.Interfaces.Select(i => i.InterfaceType.FullName).ToList()
        );
    }

    private static TypeKind GetTypeKind(TypeDefinition type)
    {
        if (type.IsInterface)
            return TypeKind.Interface;
        if (type.IsEnum)
            return TypeKind.Enum;
        if (type.IsValueType)
            return TypeKind.Struct;
        if (type.BaseType?.FullName == "System.MulticastDelegate" ||
            type.BaseType?.FullName == "System.Delegate")
            return TypeKind.Delegate;
        return TypeKind.Class;
    }

    private static List<MemberSummary> GetMembers(TypeDefinition type)
    {
        var members = new List<MemberSummary>();

        // Constructors and Methods
        foreach (var method in type.Methods.Where(m => !m.IsCompilerControlled()))
        {
            var kind = method.IsConstructor ? MemberKind.Constructor : MemberKind.Method;
            members.Add(new MemberSummary(
                method.Name,
                kind,
                method.ReturnType.FullName,
                method.Parameters.Select(ToParameterInfo).ToList(),
                method.IsStatic,
                method.IsPublic,
                GetAccessibility(method)
            ));
        }

        // Properties
        foreach (var prop in type.Properties)
        {
            var accessor = prop.GetMethod ?? prop.SetMethod;
            members.Add(new MemberSummary(
                prop.Name,
                MemberKind.Property,
                prop.PropertyType.FullName,
                prop.Parameters.Select(ToParameterInfo).ToList(),
                accessor?.IsStatic ?? false,
                accessor?.IsPublic ?? false,
                accessor is not null ? GetAccessibility(accessor) : "private"
            ));
        }

        // Fields
        foreach (var field in type.Fields.Where(f => !f.IsCompilerControlled()))
            members.Add(new MemberSummary(
                field.Name,
                MemberKind.Field,
                field.FieldType.FullName,
                [],
                field.IsStatic,
                field.IsPublic,
                GetFieldAccessibility(field)
            ));

        // Events
        foreach (var evt in type.Events)
        {
            var accessor = evt.AddMethod ?? evt.RemoveMethod;
            members.Add(new MemberSummary(
                evt.Name,
                MemberKind.Event,
                evt.EventType.FullName,
                [],
                accessor?.IsStatic ?? false,
                accessor?.IsPublic ?? false,
                accessor is not null ? GetAccessibility(accessor) : "private"
            ));
        }

        return members;
    }

    private static MemberDetail ToMemberDetail(MethodDefinition method)
    {
        return new MemberDetail(
            method.Name,
            method.IsConstructor ? MemberKind.Constructor : MemberKind.Method,
            method.ReturnType.FullName,
            method.Parameters.Select(ToParameterInfo).ToList(),
            method.IsStatic,
            method.IsPublic,
            GetAccessibility(method),
            method.CustomAttributes.Select(a => a.AttributeType.FullName).ToList(),
            method.GenericParameters.Select(p => p.Name).ToList()
        );
    }

    private static MemberDetail ToMemberDetail(PropertyDefinition property)
    {
        var accessor = property.GetMethod ?? property.SetMethod;
        return new MemberDetail(
            property.Name,
            MemberKind.Property,
            property.PropertyType.FullName,
            property.Parameters.Select(ToParameterInfo).ToList(),
            accessor?.IsStatic ?? false,
            accessor?.IsPublic ?? false,
            accessor is not null ? GetAccessibility(accessor) : "private",
            property.CustomAttributes.Select(a => a.AttributeType.FullName).ToList(),
            []
        );
    }

    private static MemberDetail ToMemberDetail(FieldDefinition field)
    {
        return new MemberDetail(
            field.Name,
            MemberKind.Field,
            field.FieldType.FullName,
            [],
            field.IsStatic,
            field.IsPublic,
            GetFieldAccessibility(field),
            field.CustomAttributes.Select(a => a.AttributeType.FullName).ToList(),
            []
        );
    }

    private static MemberDetail ToMemberDetail(EventDefinition evt)
    {
        var accessor = evt.AddMethod ?? evt.RemoveMethod;
        return new MemberDetail(
            evt.Name,
            MemberKind.Event,
            evt.EventType.FullName,
            [],
            accessor?.IsStatic ?? false,
            accessor?.IsPublic ?? false,
            accessor is not null ? GetAccessibility(accessor) : "private",
            evt.CustomAttributes.Select(a => a.AttributeType.FullName).ToList(),
            []
        );
    }

    private static ParameterInfo ToParameterInfo(ParameterDefinition param)
    {
        string? defaultValue = null;
        if (param.HasDefault && param.Constant is not null) defaultValue = param.Constant.ToString();

        return new ParameterInfo(
            param.Name,
            param.ParameterType.FullName,
            param.IsOptional,
            defaultValue
        );
    }

    private static string GetAccessibility(MethodDefinition method)
    {
        if (method.IsPublic) return "public";
        if (method.IsFamily) return "protected";
        if (method.IsFamilyOrAssembly) return "protected internal";
        if (method.IsAssembly) return "internal";
        if (method.IsFamilyAndAssembly) return "private protected";
        return "private";
    }

    private static string GetFieldAccessibility(FieldDefinition field)
    {
        if (field.IsPublic) return "public";
        if (field.IsFamily) return "protected";
        if (field.IsFamilyOrAssembly) return "protected internal";
        if (field.IsAssembly) return "internal";
        if (field.IsFamilyAndAssembly) return "private protected";
        return "private";
    }
}

internal static class CecilExtensions
{
    public static bool IsCompilerControlled(this MethodDefinition method)
    {
        return method.Name.StartsWith('<')
               || method.IsCompilerControlled
               || (method.IsSetter && method.Name.StartsWith("set_")
                                   && method.DeclaringType.Properties.Any(p => p.SetMethod == method));
    }

    public static bool IsCompilerControlled(this FieldDefinition field)
    {
        return field.Name.StartsWith('<')
               || field.IsCompilerControlled;
    }
}