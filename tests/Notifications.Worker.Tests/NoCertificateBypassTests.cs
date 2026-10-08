using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The service never replaces certificate validation, so the trust store decides everywhere.</summary>
public sealed partial class NoCertificateBypassTests
{
    [Fact]
    public void Nothing_in_the_service_names_a_certificate_validation_callback()
    {
        string service = Path.Combine(RepositoryRoot.Locate(), "src", "Services", "Notifications");
        string[] files = Directory.GetFiles(service, "*.cs", SearchOption.AllDirectories);

        files.ShouldNotBeEmpty("a scan that read nothing reports exactly what a clean tree reports");
        files
            .Where(f => Bypass().IsMatch(File.ReadAllText(f)))
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }")]
    [InlineData("HttpClientHandler.DangerousAcceptAnyServerCertificateValidator")]
    [InlineData("client.ServerCertificateValidationCallback = (_, _, _, _) => true;")]
    [InlineData("new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Accept }")]
    [InlineData("ServicePointManager.ServerCertificateValidationCallback += Accept;")]
    public void Each_spelling_of_a_bypass_is_caught(string source) => Bypass().IsMatch(source).ShouldBeTrue(source);

    // HttpClientHandler puts Custom inside the name, and its accept-anything validator names no callback at all.
    [GeneratedRegex(@"Certificate\w*ValidationCallback|DangerousAcceptAny\w*CertificateValidator")]
    private static partial Regex Bypass();
}
