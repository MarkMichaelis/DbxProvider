using System.Linq;
using IntelliTect.Dropbox;
using FluentAssertions;
using Xunit;

namespace Dbx.Core.UnitTests;

/// <summary>
/// Proves Dbx.Core is a standalone Dropbox client: it builds a
/// <see cref="DropboxServiceClient"/> directly from supplied credentials with no
/// dependency on the Dbx.Auth onboarding library or PowerShell.
/// </summary>
public class DropboxServiceClientConstructionTests
{
    [Fact]
    public void Constructor_AppKeySecretRefreshToken_BuildsClientWithoutAuth()
    {
        using var client = new DropboxServiceClient("refresh-token", "app-key", "app-secret");
        client.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_AccessToken_BuildsClient()
    {
        using var client = new DropboxServiceClient("access-token");
        client.Should().NotBeNull();
    }

    [Fact]
    public void CoreAssembly_DoesNotReferenceAuthOrPowerShell()
    {
        var referenced = typeof(DropboxServiceClient).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referenced.Should().NotContain("MarkMichaelis.Dropbox.Auth");
        referenced.Should().NotContain("System.Management.Automation");
        referenced.Should().NotContain("Microsoft.Playwright");
    }
}