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
    [InlineData("new SslStream(inner, false, (_, _, _, _) => true)")]
    [InlineData("options.CertificateChainPolicy = new X509ChainPolicy { TrustMode = CustomRootTrust };")]
    [InlineData("chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;")]
    [InlineData("policy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;")]
    [InlineData("client.CheckCertificateRevocation = false;")]
    [InlineData("SslStream stream = new(inner, false, Accept);")]
    [InlineData("options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;")]
    public void Each_spelling_of_a_bypass_is_caught(string source) => Bypass().IsMatch(source).ShouldBeTrue(source);

    // A callback under any name, any SslStream (the service builds none), a replaced chain policy, or no revocation.
    [GeneratedRegex(@"Certificate\w*ValidationCallback|DangerousAcceptAny\w*CertificateValidator|\bSslStream\b" +
        @"|CertificateChainPolicy|X509ChainTrustMode|X509VerificationFlags" +
        @"|CheckCertificateRevocation\s*=\s*false|X509RevocationMode\.NoCheck")]
    private static partial Regex Bypass();
}
