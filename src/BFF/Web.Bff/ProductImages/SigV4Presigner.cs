using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Web.Bff.ProductImages;

/// <summary>AWS Signature Version 4 in its query-string form, for one object and one method.</summary>
/// <remarks>
/// A local computation, so handing out a URL takes no runtime dependency on the store (ADR-106). Hand-written
/// rather than taken from the SDK because the signature must cover <c>Content-Length</c> (§10.8), which the SDK's
/// pre-signer does not sign; the AWS documentation's published example is the test that this is the algorithm.
/// </remarks>
public static class SigV4Presigner
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    private const string Service = "s3";

    /// <summary>The URL for <paramref name="method"/> on <paramref name="resource"/>, good for a lifetime.</summary>
    /// <param name="resource">The absolute URL of the object, its path already in the store's addressing style.</param>
    /// <param name="signedHeaders">Header names and values the caller must send, besides <c>Host</c>.</param>
    public static Uri Presign(
        string method,
        Uri resource,
        IReadOnlyDictionary<string, string> signedHeaders,
        string accessKey,
        string secretKey,
        string region,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        string stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string date = stamp[..8];
        string scope = $"{date}/{region}/{Service}/aws4_request";

        SortedDictionary<string, string> headers = new(StringComparer.Ordinal) { ["host"] = resource.Authority };
        foreach ((string name, string value) in signedHeaders)
            headers[name.ToLowerInvariant()] = value.Trim();

        string signedNames = string.Join(';', headers.Keys);

        SortedDictionary<string, string> query = new(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = Algorithm,
            ["X-Amz-Credential"] = $"{accessKey}/{scope}",
            ["X-Amz-Date"] = stamp,
            ["X-Amz-Expires"] = ((long)lifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = signedNames
        };

        string canonicalQuery = string.Join('&', query.Select(p => $"{Encode(p.Key)}={Encode(p.Value)}"));

        // S3 does not normalise or double-encode the path, so each segment is encoded once and the slashes stay.
        string canonicalPath = string.Join(
            '/',
            resource.AbsolutePath.Split('/').Select(segment => Encode(Uri.UnescapeDataString(segment))));

        StringBuilder canonical = new();
        canonical.Append(method).Append('\n');
        canonical.Append(canonicalPath).Append('\n');
        canonical.Append(canonicalQuery).Append('\n');
        foreach ((string name, string value) in headers)
            canonical.Append(name).Append(':').Append(value).Append('\n');
        canonical.Append('\n').Append(signedNames).Append('\n').Append("UNSIGNED-PAYLOAD");

        string requestHash = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        string toSign = $"{Algorithm}\n{stamp}\n{scope}\n{requestHash}";

        byte[] key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        key = Hmac(key, region);
        key = Hmac(key, Service);
        key = Hmac(key, "aws4_request");

        string signature = Hex(Hmac(key, toSign));

        return new UriBuilder(resource) { Query = $"{canonicalQuery}&X-Amz-Signature={signature}" }.Uri;
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    // RFC 3986's unreserved set and nothing else, upper-case hex: the form SigV4 canonicalises to.
    private static string Encode(string value)
    {
        StringBuilder encoded = new();
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~')
                encoded.Append(c);
            else
                encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return encoded.ToString();
    }
}
