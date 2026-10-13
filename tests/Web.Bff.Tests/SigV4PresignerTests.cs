using Shouldly;
using Web.Bff.ProductImages;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The signer against AWS's own published example, which is what makes it the algorithm.</summary>
public class SigV4PresignerTests
{
    // The credentials and expected signature are from AWS's "Authenticating Requests: Using Query Parameters"
    // documentation, whose example GETs test.txt from examplebucket and signs the host alone.
    private const string DocumentedAccessKey = "AKIAIOSFODNN7EXAMPLE";

    private const string DocumentedSecret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    private const string DocumentedSignature = "aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404";

    private static readonly DateTimeOffset DocumentedTime = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_documented_example_signs_to_the_documented_signature()
    {
        Uri url = SigV4Presigner.Presign(
            "GET",
            new Uri("https://examplebucket.s3.amazonaws.com/test.txt"),
            new Dictionary<string, string>(),
            DocumentedAccessKey,
            DocumentedSecret,
            "us-east-1",
            DocumentedTime,
            TimeSpan.FromSeconds(86400));

        url.Query.ShouldContain($"X-Amz-Signature={DocumentedSignature}");
        url.Query.ShouldContain("X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request");
        url.Query.ShouldContain("X-Amz-SignedHeaders=host");
    }

    [Fact]
    public void A_signed_header_changes_the_signature_and_is_named_in_the_URL()
    {
        Uri unsigned = Sign("PUT", new Dictionary<string, string>());
        Uri signed = Sign("PUT", new Dictionary<string, string> { ["Content-Length"] = "1024" });
        Uri longer = Sign("PUT", new Dictionary<string, string> { ["Content-Length"] = "1025" });

        signed.Query.ShouldContain("X-Amz-SignedHeaders=content-length%3Bhost");
        SignatureOf(signed).ShouldNotBe(SignatureOf(unsigned));

        // The point of signing the length: a larger body is a different request the store refuses.
        SignatureOf(longer).ShouldNotBe(SignatureOf(signed));
    }

    [Fact]
    public void The_method_and_the_key_are_signed()
    {
        string put = SignatureOf(Sign("PUT", new Dictionary<string, string>()));

        SignatureOf(Sign("GET", new Dictionary<string, string>())).ShouldNotBe(put);
        SignatureOf(
            SigV4Presigner.Presign(
                "PUT",
                new Uri("https://examplebucket.s3.amazonaws.com/other.txt"),
                new Dictionary<string, string>(),
                DocumentedAccessKey,
                DocumentedSecret,
                "us-east-1",
                DocumentedTime,
                TimeSpan.FromSeconds(300))).ShouldNotBe(put);
    }

    [Fact]
    public void The_lifetime_is_the_URL_s_expiry()
    {
        Sign("PUT", new Dictionary<string, string>()).Query.ShouldContain("X-Amz-Expires=300");
    }

    private static Uri Sign(string method, Dictionary<string, string> headers) =>
        SigV4Presigner.Presign(
            method,
            new Uri("https://examplebucket.s3.amazonaws.com/test.txt"),
            headers,
            DocumentedAccessKey,
            DocumentedSecret,
            "us-east-1",
            DocumentedTime,
            TimeSpan.FromSeconds(300));

    private static string SignatureOf(Uri url) => url.Query.Split("X-Amz-Signature=")[1];
}
