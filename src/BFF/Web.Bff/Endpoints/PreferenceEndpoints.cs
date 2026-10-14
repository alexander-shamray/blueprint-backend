using Common.Web;
using FluentValidation;
using Microsoft.Net.Http.Headers;
using Notifications.Preferences.V1;
using Web.Bff.Preferences;
using PreferencesGrpc = Notifications.Preferences.V1.Preferences;

namespace Web.Bff.Endpoints;

/// <summary>§10.11's preferences read and write: one hop each, under the caller's token (ADR-120).</summary>
public static class PreferenceEndpoints
{
    /// <summary>The 412's code: the pair changed since the client read it; the body carries it now.</summary>
    public const string PreconditionFailedCode = "preference.precondition_failed";

    /// <summary>The 422's code: the deployment must send the pair, so it takes no preference (§10.5).</summary>
    public const string RequiredCode = "preference.required";

    public static void MapPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/notification-preferences")
            .WithTags("Notification preferences")
            // Fail closed at the group (§11.4); the subject is the forwarded token's, never the request's.
            .RequireAuthorization();

        group
            .MapGet(
                "/",
                async (PreferencesGrpc.PreferencesClient preferences, CancellationToken ct) =>
                {
                    PreferencesReply reply = await preferences.GetAsync(
                        new GetPreferencesRequest(),
                        cancellationToken: ct);

                    return Results.Ok(new PreferencesView([.. reply.Preferences.Select(View)]));
                })
            .WithName("GetNotificationPreferences");

        group
            .MapPut(
                "/{topic}/{channel}",
                async (
                    string topic,
                    string channel,
                    PreferenceChange body,
                    HttpContext context,
                    IValidator<PreferenceWrite> validator,
                    PreferencesGrpc.PreferencesClient preferences,
                    CancellationToken ct) =>
                {
                    PreferenceWrite write = new(topic, channel, body.OptedIn);
                    await validator.ValidateAndThrowAsync(write, ct);

                    Precondition precondition = context.Features.Get<Precondition>()!;

                    SetPreferenceReply reply = await preferences.SetAsync(
                        new SetPreferenceRequest
                        {
                            Topic = topic,
                            Channel = channel,
                            OptedIn = write.OptedIn!.Value,
                            ReadVersion = precondition.ReadVersion?.ToString("D") ?? ""
                        },
                        cancellationToken: ct);

                    return Answer(reply, write, context);
                })
            .AddEndpointFilter<PreconditionFilter>()
            // Appended only while the version read is the newest, so a repeat is answered as it was (ADR-120).
            .RetrySafe(RetrySafety.Convergent)
            .WithName("PutNotificationPreference");
    }

    /// <summary>200 with the pair for a write applied or repeated, 412 with it for a lost race.</summary>
    /// <remarks>Checked rather than trusted, as §9.7's rule 6 has every reply; a violation is a 500.</remarks>
    private static IResult Answer(SetPreferenceReply reply, PreferenceWrite write, HttpContext context)
    {
        switch (reply.Outcome)
        {
            case SetPreferenceReply.Types.Outcome.Applied or SetPreferenceReply.Types.Outcome.Repeated
                when View(reply.Preference) is { Source: PreferenceView.Set } view &&
                     view.Topic == write.Topic && view.Channel == write.Channel && view.OptedIn == write.OptedIn:
                context.Response.Headers.ETag = Tag(view.Version!);
                return Results.Ok(view);

            case SetPreferenceReply.Types.Outcome.PreconditionFailed
                when View(reply.Preference) is var current &&
                     current.Topic == write.Topic && current.Channel == write.Channel:
                if (current.Version is not null)
                    context.Response.Headers.ETag = Tag(current.Version);

                return Results.Problem(
                    detail: "The preference changed since it was read. Show the subject the preference this reply " +
                            "carries, and send their choice again under its version.",
                    statusCode: StatusCodes.Status412PreconditionFailed,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = PreconditionFailedCode,
                        ["preference"] = current
                    });

            case SetPreferenceReply.Types.Outcome.Required:
                return Results.Problem(
                    detail: "This deployment is obliged to send this topic on this channel, so it takes no " +
                            "preference.",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?> { ["code"] = RequiredCode });

            default:
                throw new InvalidOperationException(
                    $"Notifications answered a preference write with outcome {reply.Outcome} and a preference that " +
                    "does not match the write. preferences.proto gives an applied or repeated write its own pair, " +
                    "value and version, so this is a contract violation rather than an answer.");
        }
    }

    // The strong entity tag a write's If-Match carries back (§10.11).
    private static string Tag(string version) => $"\"{version}\"";

    /// <summary>A pair as the wire carries it, refused where preferences.proto forbids the shape (§9.7).</summary>
    private static PreferenceView View(Preference? preference)
    {
        string source = preference?.Source switch
        {
            Preference.Types.Source.Required => PreferenceView.Required,
            Preference.Types.Source.Default => PreferenceView.Default,
            Preference.Types.Source.Set => PreferenceView.Set,
            _ => throw Violation(preference)
        };

        bool set = source == PreferenceView.Set;
        bool versioned = Guid.TryParseExact(preference!.Version, "D", out _);

        // A set pair carries its entry's version and time, and any other carries neither.
        if (set != versioned || set != (preference.SetAt is not null) ||
            !PreferenceRules.Topics.Contains(preference.Topic) ||
            !PreferenceRules.Channels.Contains(preference.Channel))
        {
            throw Violation(preference);
        }

        return new PreferenceView(
            preference.Topic,
            preference.Channel,
            preference.OptedIn,
            source,
            preference.SetAt?.ToDateTimeOffset(),
            set ? preference.Version : null);
    }

    private static InvalidOperationException Violation(Preference? preference) =>
        new(
            $"Notifications answered a preference of source {preference?.Source} that preferences.proto " +
            "forbids: a set pair carries a version and a time, any other carries neither, and every pair is of " +
            "the vocabulary. This is a contract violation rather than an answer.");
}

/// <summary>The read's body: every pair, in the vocabulary's order.</summary>
public sealed record PreferencesView(IReadOnlyList<PreferenceView> Preferences);

/// <summary>One pair as a client is shown it; <c>version</c> is what its <c>If-Match</c> names, quoted.</summary>
public sealed record PreferenceView(
    string Topic,
    string Channel,
    bool OptedIn,
    string Source,
    DateTimeOffset? SetAt,
    string? Version)
{
    public const string Required = "required";
    public const string Default = "default";
    public const string Set = "set";
}
