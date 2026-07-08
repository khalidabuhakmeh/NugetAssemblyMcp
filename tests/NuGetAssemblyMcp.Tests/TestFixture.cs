using NuGetAssemblyMcp.Services;
using NuGetAssemblyMcp.Services.Models;

namespace NuGetAssemblyMcp.Tests;

public class DuendePackageFixture : IAsyncLifetime
{
    private PackageContent? _content;

    public PackageContent Content =>
        _content ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync first.");

    public async Task InitializeAsync()
    {
        var service = new NuGetPackageService();
        _content = await service.LoadPackageAsync("Duende.IdentityServer", "7.0.0");
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}