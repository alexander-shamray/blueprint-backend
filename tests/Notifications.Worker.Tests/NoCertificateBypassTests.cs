using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The service never replaces certificate validation, so the trust store decides everywhere.</summary>
public sealed class NoCertificateBypassTests
{
    [Fact]
    public void Nothing_in_the_service_names_a_certificate_validation_callback()
    {
        string service = Path.Combine(RepositoryRoot.Locate(), "src", "Services", "Notifications");
        string[] files = Directory.GetFiles(service, "*.cs", SearchOption.AllDirectories);

        files.ShouldNotBeEmpty("a scan that read nothing reports exactly what a clean tree reports");
        files
            .Where(f => File.ReadAllText(f).Contains("CertificateValidationCallback", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }
}
