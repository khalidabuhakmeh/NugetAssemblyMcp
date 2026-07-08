using NuGetAssemblyMcp.Services;
using NuGetAssemblyMcp.Services.Models;
using Xunit.Abstractions;

namespace NuGetAssemblyMcp.Tests;

public class AssemblyInspectionServiceTests(DuendePackageFixture fixture, ITestOutputHelper output)
    : IClassFixture<DuendePackageFixture>
{
    private readonly AssemblyInspectionService _service = new();

    [Fact]
    public void GetNamespaces_ReturnsDuendeNamespaces()
    {
        var namespaces = _service.GetNamespaces(fixture.Content.DllPath);

        output.WriteLine($"Found {namespaces.Count} namespaces:");
        foreach (var ns in namespaces) output.WriteLine($"  - {ns}");

        Assert.NotNull(namespaces);
        Assert.NotEmpty(namespaces);
        Assert.Contains(namespaces, ns => ns == "Duende.IdentityServer");
        Assert.Contains(namespaces, ns => ns == "Duende.IdentityServer.Configuration");
    }

    [Fact]
    public void GetTypes_InRootNamespace_ReturnsKnownTypes()
    {
        var types = _service.GetTypes(fixture.Content.DllPath, "Duende.IdentityServer");

        output.WriteLine($"Found {types.Count} types in Duende.IdentityServer:");
        foreach (var type in types.Take(20)) output.WriteLine($"  - {type.FullName} ({type.Kind})");
        if (types.Count > 20)
            output.WriteLine($"  ... and {types.Count - 20} more");

        Assert.NotNull(types);
        Assert.NotEmpty(types);
        Assert.Contains(types, t => t.Name == "IdentityServerTools");
    }

    [Fact]
    public void GetTypes_FilterByNamespace_OnlyReturnsMatchingTypes()
    {
        const string targetNamespace = "Duende.IdentityServer.Configuration";
        var types = _service.GetTypes(fixture.Content.DllPath, targetNamespace);

        output.WriteLine($"Found {types.Count} types in {targetNamespace}:");
        foreach (var type in types) output.WriteLine($"  - {type.Name} ({type.Kind})");

        Assert.NotNull(types);
        Assert.NotEmpty(types);
        Assert.All(types, t => Assert.Equal(targetNamespace, t.Namespace));
    }

    [Fact]
    public void GetTypeDetail_IdentityServerOptions_ReturnsProperties()
    {
        var detail = _service.GetTypeDetail(
            fixture.Content.DllPath,
            "Duende.IdentityServer.Configuration.IdentityServerOptions");

        output.WriteLine($"Type: {detail.FullName}");
        output.WriteLine($"Kind: {detail.Kind}");
        output.WriteLine($"Base: {detail.BaseTypeName}");
        output.WriteLine($"Interfaces: {string.Join(", ", detail.InterfaceNames)}");
        output.WriteLine($"Generic Params: {string.Join(", ", detail.GenericParameters)}");
        output.WriteLine($"Members ({detail.Members.Count}):");
        foreach (var member in detail.Members.Where(m => m.IsPublic).Take(30))
            output.WriteLine($"  [{member.MemberKind}] {member.Name} : {member.ReturnType}");

        Assert.NotNull(detail);
        Assert.Equal("IdentityServerOptions", detail.Name);
        Assert.Equal("Duende.IdentityServer.Configuration", detail.Namespace);
        Assert.Equal(TypeKind.Class, detail.Kind);
        Assert.NotEmpty(detail.Members);
        Assert.Contains(detail.Members, m => m.Name == "Endpoints" && m.MemberKind == MemberKind.Property);
    }

    [Fact]
    public void SearchTypes_FindsMatchingTypes()
    {
        var results = _service.SearchTypes(fixture.Content.DllPath, "Options");

        output.WriteLine($"Search 'Options' found {results.Count} types:");
        foreach (var type in results.Take(20)) output.WriteLine($"  - {type.FullName} ({type.Kind})");
        if (results.Count > 20)
            output.WriteLine($"  ... and {results.Count - 20} more");

        Assert.NotNull(results);
        Assert.NotEmpty(results);
        Assert.Contains(results, t => t.Name == "IdentityServerOptions");
        Assert.All(results, t => Assert.Contains("Options", t.FullName, StringComparison.OrdinalIgnoreCase));
    }
}