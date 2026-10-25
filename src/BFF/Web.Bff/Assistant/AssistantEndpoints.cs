using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Common.Application;
using Common.Assistant;
using Common.Assistant.Protocol;
using Common.Assistant.Tools;
using Common.Web;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Web.Bff.Assistant;

/// <summary>The AI assistant design's §5.1 protocol at <c>/v1/assistant</c>, for a signed-in customer.</summary>
/// <remarks>
/// With the assistant off only the status route is mapped, so every other path is a 404 and nothing can open a
/// conversation (AI assistant design §9.1). <c>POST …/confirmations</c> is reserved (design §6.3) and stays a 404.
/// </remarks>
public static class AssistantEndpoints
{
    /// <summary>The longest message a customer may send, in characters.</summary>
    public const int MaxTextLength = 4_000;

    /// <summary>The most catalogue entries a client may send.</summary>
    public const int MaxCatalogue = 200;

    /// <summary>The rate limiter policy on the two routes that start work: opening a conversation and a turn.</summary>
    public const string StartPolicy = "assistant-start";

    /// <summary>Inside <c>ServiceOptions.OperationTimeout</c>, so the stream ends before a 504 does.</summary>
    public static readonly TimeSpan StreamLifetime = ServiceOptions.OperationTimeout - TimeSpan.FromSeconds(5);

    /// <summary>The reconnection delay the stream asks of a client (§10.7).</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(1);

    // The protocol's own options, so a client's mirror reads the shape the golden files pin (ADR-149).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web);

    public static void MapAssistantEndpoints(this WebApplication app)
    {
        AssistantRuntime runtime = app.Services.GetRequiredService<AssistantRuntime>();

        RouteGroupBuilder group = app
            .MapGroup("/v1/assistant")
            .WithTags("Assistant")
            // Fail closed at the group (§11.4); the caller below is the principal's, never the request's (§10.7).
            .RequireAuthorization();

        group.MapGet(
            "/",
            async (CancellationToken ct) =>
                runtime.Core is { } on ? await on.StatusAsync(ct) : AssistantStatus.Off);

        if (runtime.Core is not { } core)
            return;

        group
            .MapPost(
                "/conversations",
                Results<Ok<ConversationOpened>, ProblemHttpResult> (OpenConversation? request, ICurrentUser user) =>
                {
                    if (Invalid(request) is { } problem)
                        return problem;

                    try
                    {
                        return core.Open(CallerOf(user), request!) is { } session
                            ? TypedResults.Ok(new ConversationOpened(session.Id))
                            : TypedResults.Problem(
                                statusCode: StatusCodes.Status429TooManyRequests,
                                title: "Too many conversations",
                                detail: "Every conversation this host can hold is in use; try again later.");
                    }
                    catch (ToolRefusedException refused)
                    {
                        return TypedResults.Problem(
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "A client tool was refused",
                            detail: refused.Message);
                    }
                })
            .RequireRateLimiting(StartPolicy);

        group.MapGet(
            "/conversations/{id}",
            Results<Ok<ConversationView>, ProblemHttpResult> (string id, ICurrentUser user) =>
                core.Find(CallerOf(user), id) is { } session ? TypedResults.Ok(session.View()) : Unknown(id));

        group
            .MapPost(
                "/conversations/{id}/turns",
                Results<Accepted, ProblemHttpResult> (string id, StartTurn? request, ICurrentUser user) =>
                {
                    if (core.Find(CallerOf(user), id) is not { } session)
                        return Unknown(id);

                    if (request is null || string.IsNullOrWhiteSpace(request.Text) ||
                        request.Text.Length > MaxTextLength)
                    {
                        return TypedResults.Problem(
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "Not a message",
                            detail: $"A turn needs text, at most {MaxTextLength} characters.");
                    }

                    return session.TryStart(request)
                        ? TypedResults.Accepted((string?)null)
                        : TypedResults.Problem(
                            statusCode: StatusCodes.Status409Conflict,
                            title: "A turn is running",
                            detail: "Wait for the running turn to end, or cancel it.");
                })
            .RequireRateLimiting(StartPolicy);

        group.MapGet(
            "/conversations/{id}/events",
            async Task (string id, long? after, HttpContext context, ICurrentUser user, CancellationToken ct) =>
            {
                if (core.Find(CallerOf(user), id) is not { } session)
                {
                    await Unknown(id).ExecuteAsync(context);

                    return;
                }

                await StreamAsync(context, session, ParseLastEventId(context) ?? after ?? 0, ct);
            });

        group.MapPost(
            "/conversations/{id}/tool-results",
            Results<NoContent, ProblemHttpResult> (string id, ClientToolResult? result, ICurrentUser user) =>
            {
                if (core.Find(CallerOf(user), id) is not { } session)
                    return Unknown(id);

                if (result is null || result.CallId is null || result.Content is null)
                {
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Not a tool result",
                        detail: "Send callId, ok and content.");
                }

                return session.TryAnswer(result)
                    ? TypedResults.NoContent()
                    : TypedResults.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "No such tool call",
                        detail: result.CallId);
            });

        group.MapPost(
            "/conversations/{id}/cancel",
            Results<NoContent, ProblemHttpResult> (string id, ICurrentUser user) =>
            {
                if (core.Find(CallerOf(user), id) is not { } session)
                    return Unknown(id);

                session.Cancel();

                return TypedResults.NoContent();
            });

        group.MapDelete(
            "/conversations/{id}",
            Results<NoContent, ProblemHttpResult> (string id, ICurrentUser user) =>
                core.Delete(CallerOf(user), id) ? TypedResults.NoContent() : Unknown(id));
    }

    // The subject alone: the core sends no caller to the provider, and a display name would be personal data.
    private static Caller CallerOf(ICurrentUser user)
    {
        string subject = user.Id.ToString();

        return new Caller(subject, subject);
    }

    // ADR-108's shape: the stream ends inside the deadline and the client reconnects from Last-Event-ID.
    private static async Task StreamAsync(
        HttpContext context,
        AssistantSession session,
        long afterId,
        CancellationToken ct)
    {
        HttpResponse response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(StreamLifetime);

        await response.WriteAsync($"retry: {(int)RetryAfter.TotalMilliseconds}\n\n", ct);
        await response.Body.FlushAsync(ct);

        try
        {
            await SseFormatter.WriteAsync(Events(session, afterId, lifetime.Token), response.Body, lifetime.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The lifetime, not the client: the response ends and EventSource reconnects.
        }
    }

    private static async IAsyncEnumerable<SseItem<string>> Events(
        AssistantSession session,
        long afterId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach ((long id, AssistantEvent value) in session.Events(afterId, ct))
        {
            yield return new SseItem<string>(JsonSerializer.Serialize(value, value.GetType(), Json), value.EventName)
            {
                EventId = id.ToString(CultureInfo.InvariantCulture)
            };
        }
    }

    private static ProblemHttpResult? Invalid(OpenConversation? request)
    {
        string? why = request switch
        {
            null => "The body is not a conversation to open.",
            { Client: null } => "Send client, its name and version.",
            { Locale: null or "" } => "Send locale, a BCP 47 tag such as en-GB.",
            { Catalogue: null } => "Send catalogue, empty when the client has none.",
            { Catalogue.Count: > MaxCatalogue } => $"A catalogue holds at most {MaxCatalogue} entries.",
            { ClientTools: null } => "Send clientTools, empty when the client has none.",
            _ when request.Catalogue.Any(e => e is null || e.Id is null || e.Label is null || e.Help is null ||
                e.Where is null) => "Every catalogue entry needs id, label, where and help.",
            _ when request.ClientTools.Any(t => t is null || t.Name is null || t.Title is null ||
                t.Description is null || t.InputSchema is null || t.Annotations is null) =>
                "Every client tool needs name, title, description, inputSchema and annotations.",
            _ => null
        };

        return why is null
            ? null
            : TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Not a conversation",
                detail: why);
    }

    // Another customer's id answers as an unknown one does, so an id reveals nothing (§10.7).
    private static ProblemHttpResult Unknown(string id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such conversation", detail: id);

    private static long? ParseLastEventId(HttpContext context) =>
        long.TryParse(
            context.Request.Headers["Last-Event-ID"].FirstOrDefault(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long value)
            ? value
            : null;
}
