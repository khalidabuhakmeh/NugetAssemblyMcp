using NuGetAssemblyMcp.Services;
using NuGetAssemblyMcp.Services.Models;
using Xunit.Abstractions;

namespace NuGetAssemblyMcp.Tests;

public class XmlDocServiceTests(DuendePackageFixture fixture, ITestOutputHelper output)
    : IClassFixture<DuendePackageFixture>
{
    [Fact]
    public void GetTypeDocumentation_ReturnsNonNullForKnownType()
    {
        Assert.NotNull(fixture.Content.XmlDocPath);
        Assert.True(File.Exists(fixture.Content.XmlDocPath),
            $"XML doc file should exist at {fixture.Content.XmlDocPath}");

        var xmlDocService = new XmlDocService(fixture.Content.XmlDocPath);

        var summary = xmlDocService.GetTypeDocumentation("Duende.IdentityServer.Configuration.IdentityServerOptions");

        output.WriteLine("Type: Duende.IdentityServer.Configuration.IdentityServerOptions");
        output.WriteLine($"Summary: {summary}");

        Assert.NotNull(summary);
        Assert.NotEmpty(summary);
    }

    [Fact]
    public void GetDocumentation_ForProperty_ReturnsDescription()
    {
        Assert.NotNull(fixture.Content.XmlDocPath);

        var xmlDocService = new XmlDocService(fixture.Content.XmlDocPath);

        var doc = xmlDocService.GetDocumentation(
            "P:Duende.IdentityServer.Configuration.IdentityServerOptions.Endpoints");

        output.WriteLine("Property: IdentityServerOptions.Endpoints");
        output.WriteLine($"  Summary: {doc?.Summary}");
        output.WriteLine($"  Remarks: {doc?.Remarks ?? "(none)"}");
        output.WriteLine($"  Value: {doc?.Value ?? "(none)"}");

        Assert.NotNull(doc);
        Assert.NotNull(doc.Summary);
        Assert.NotEmpty(doc.Summary);
    }

    [Fact]
    public void GetDocumentation_ForFullType_ShowsAllMembers()
    {
        Assert.NotNull(fixture.Content.XmlDocPath);

        var xmlDocService = new XmlDocService(fixture.Content.XmlDocPath);
        var assemblyService = new AssemblyInspectionService();
        var detail = assemblyService.GetTypeDetail(
            fixture.Content.DllPath,
            "Duende.IdentityServer.Configuration.IdentityServerOptions");

        output.WriteLine("=== IdentityServerOptions Full Documentation ===");
        output.WriteLine(" ");

        var typeSummary =
            xmlDocService.GetTypeDocumentation("Duende.IdentityServer.Configuration.IdentityServerOptions");
        output.WriteLine($"Type Summary: {typeSummary}");
        output.WriteLine(" ");

        output.WriteLine("--- Properties ---");
        foreach (var member in detail.Members
                     .Where(m => m.MemberKind == MemberKind.Property && m.IsPublic))
        {
            var memberId = $"P:Duende.IdentityServer.Configuration.IdentityServerOptions.{member.Name}";
            var doc = xmlDocService.GetDocumentation(memberId);
            output.WriteLine($"  {member.Name} ({member.ReturnType})");
            if (doc?.Summary is not null)
                output.WriteLine($"    Summary: {doc.Summary}");
            if (doc?.Remarks is not null)
                output.WriteLine($"    Remarks: {doc.Remarks}");
            output.WriteLine(" ");
        }

        output.WriteLine("--- Methods ---");
        foreach (var member in detail.Members
                     .Where(m => m.MemberKind == MemberKind.Method && m.IsPublic)
                     .Take(10))
        {
            var memberId = $"M:Duende.IdentityServer.Configuration.IdentityServerOptions.{member.Name}";
            var doc = xmlDocService.GetDocumentation(memberId);
            output.WriteLine($"  {member.Name}() -> {member.ReturnType}");
            if (doc?.Summary is not null)
                output.WriteLine($"    Summary: {doc.Summary}");
            if (doc?.Parameters.Count > 0)
                foreach (var (paramName, paramDoc) in doc.Parameters)
                    output.WriteLine($"    Param '{paramName}': {paramDoc}");
            if (doc?.Returns is not null)
                output.WriteLine($"    Returns: {doc.Returns}");
            output.WriteLine(" ");
        }

        Assert.NotNull(typeSummary);
    }

    [Fact]
    public void GetDocumentation_ForNonExistentMember_ReturnsNull()
    {
        var xmlDocService = new XmlDocService(fixture.Content.XmlDocPath);

        var doc = xmlDocService.GetDocumentation("T:Duende.IdentityServer.ThisTypeDoesNotExist");

        output.WriteLine("Looked up non-existent type: T:Duende.IdentityServer.ThisTypeDoesNotExist");
        output.WriteLine($"Result: {(doc is null ? "null (expected)" : "NOT NULL (unexpected)")}");

        Assert.Null(doc);
    }
}