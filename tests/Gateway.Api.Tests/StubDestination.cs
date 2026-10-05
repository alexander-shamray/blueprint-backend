using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>A real server on a loopback port that records each path and answers 204 unless asked for a body.</summary>
/// <remarks>A listener rather than an address that refuses, which costs seconds a request (§12.4).</remarks>
public sealed class StubDestination : IAsyncLifetime
{
    /// <summary>Asks for a body of this many bytes instead of the default 204.</summary>
    public const string BodySizeQuery = "body";

    /// <summary>Asks for a declared <c>Content-Encoding</c>; only <c>gzip</c> is applied to the body.</summary>
    public const string ContentEncodingQuery = "encoding";

    /// <summary>Asks for the body under <c>Cache-Control: no-transform</c>.</summary>
    public const string NoTransformQuery = "notransform";

    /// <summary>Asks for a <c>Vary</c> header of this value beside the body.</summary>
    public const string VaryQuery = "vary";

    /// <summary>Asks for no answer at all, until the caller gives up.</summary>
    public const string StallQuery = "stall";

    private readonly ConcurrentQueue<string> _paths = new();
    private WebApplication? _app;

    /// <summary>The base address to point a YARP cluster at.</summary>
    public string Address { get; private set; } = string.Empty;

    /// <summary>Every path this server has been asked for, in arrival order.</summary>
    public IReadOnlyCollection<string> ReceivedPaths => _paths;

    public async ValueTask InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        // Port 0, so each class fixture gets a free port of its own.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();

        app.Use(async (context, next) =>
        {
            _paths.Enqueue(context.Request.Path.Value ?? string.Empty);

            await next();
        });

        // A query string, which YARP forwards untouched, so nothing is reset between tests in a class.
        app.MapFallback(async (HttpContext context) =>
        {
            if (context.Request.Query.ContainsKey(StallQuery))
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);

            if (!int.TryParse(context.Request.Query[BodySizeQuery], out int size))
                return Results.NoContent();

            string? vary = context.Request.Query[VaryQuery];

            if (!string.IsNullOrEmpty(vary))
                context.Response.Headers.Vary = vary;

            if (context.Request.Query.ContainsKey(NoTransformQuery))
            {
                context.Response.Headers.CacheControl = "no-transform";

                return Results.Text(new string('a', size), "application/json");
            }

            string? declared = context.Request.Query[ContentEncodingQuery];

            if (!string.IsNullOrEmpty(declared))
            {
                context.Response.Headers.ContentEncoding = declared;

                return declared == "gzip"
                    ? Results.Bytes(GzipOf(new string('a', size)), "application/json")
                    : Results.Text(new string('a', size), "application/json");
            }

            // One repeated character, so the encoded form is unmistakably smaller than the plain one.
            return Results.Text(new string('a', size), "application/json");
        });

        await app.StartAsync();

        Address = app.Urls.First();
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private static byte[] GzipOf(string body)
    {
        using MemoryStream buffer = new();

        using (GZipStream compressor = new(buffer, CompressionLevel.Fastest, leaveOpen: true))
            compressor.Write(Encoding.UTF8.GetBytes(body));

        // Not a spread: a MemoryStream is not a sequence, and the spread fails to compile on it (CS9212).
        return buffer.ToArray();
    }
}
