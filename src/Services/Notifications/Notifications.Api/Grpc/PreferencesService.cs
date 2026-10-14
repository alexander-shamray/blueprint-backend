using Common.Application;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Notifications.Application.Preferences;
using Notifications.Application.Preferences.GetPreferences;
using Notifications.Application.Preferences.SetPreference;
using Notifications.Preferences.V1;
using PreferencesGrpc = Notifications.Preferences.V1.Preferences;

namespace Notifications.Api.Grpc;

/// <summary>ADR-120's preferences read and write, a transport adapter under §4.2's gate.</summary>
/// <remarks>
/// Under the caller's own token, as <see cref="DevicesService"/> is and for its reason; no permission, since any
/// signed-in subject reads and sets its own (§11.5).
/// </remarks>
[Authorize]
internal sealed class PreferencesService(IDispatcher dispatcher, ICurrentUser user) : PreferencesGrpc.PreferencesBase
{
    /// <summary>The claim naming the client a token was issued to, which an entry records (ADR-120).</summary>
    /// <remarks>Read under its own name: the inbound map JwtBearer applies (§11.3) has no entry for it.</remarks>
    public const string ClientClaim = "azp";

    public override async Task<PreferencesReply> Get(GetPreferencesRequest request, ServerCallContext context)
    {
        IReadOnlyList<PreferenceState> states =
            await dispatcher.QueryAsync(new GetPreferencesQuery(user.Id), context.CancellationToken);

        PreferencesReply reply = new();
        reply.Preferences.AddRange(states.Select(Wire));

        return reply;
    }

    public override async Task<SetPreferenceReply> Set(SetPreferenceRequest request, ServerCallContext context)
    {
        Result<PreferenceWrite> written = await dispatcher.SendAsync(
            new SetPreferenceCommand(
                user.Id,
                request.Topic,
                request.Channel,
                request.OptedIn,
                Version(request.ReadVersion),
                Client(context)),
            context.CancellationToken);

        // The one refusal the handler returns rather than throws; anything else is this method's defect.
        if (written.IsFailure)
        {
            return written.Error.Code == SetPreferenceHandler.RequiredCode
                ? new SetPreferenceReply { Outcome = SetPreferenceReply.Types.Outcome.Required }
                : throw new InvalidOperationException($"SetPreferenceHandler refused with {written.Error.Code}.");
        }

        return new SetPreferenceReply
        {
            Outcome = written.Value.Outcome switch
            {
                ConsentAppendOutcome.Appended => SetPreferenceReply.Types.Outcome.Applied,
                ConsentAppendOutcome.Repeated => SetPreferenceReply.Types.Outcome.Repeated,
                _ => SetPreferenceReply.Types.Outcome.PreconditionFailed
            },
            Preference = Wire(written.Value.State)
        };
    }

    // Parsed here, as a wire concern; empty is the contract's If-None-Match, and "D" its canonical text form.
    private static Guid? Version(string text) =>
        text.Length == 0 ? null
        : Guid.TryParseExact(text, "D", out Guid id) ? id
        : throw new RpcException(new Status(StatusCode.InvalidArgument, "read_version is not a GUID."));

    // From the authenticated identities alone, as HttpContextCurrentUser reads the subject (§11.4); a missing claim
    // leaves the command's validator to refuse it.
    private static string Client(ServerCallContext context) =>
        context.GetHttpContext().User.Identities
            .Where(identity => identity.IsAuthenticated)
            .Select(identity => identity.FindFirst(ClientClaim)?.Value)
            .FirstOrDefault(value => value is not null) ?? "";

    private static Preference Wire(PreferenceState state) =>
        new()
        {
            Topic = state.Topic,
            Channel = state.Channel,
            OptedIn = state.OptedIn,
            Source = state.Source switch
            {
                PreferenceSource.Required => Preference.Types.Source.Required,
                PreferenceSource.Default => Preference.Types.Source.Default,
                _ => Preference.Types.Source.Set
            },
            SetAt = state.SetAt is { } setAt ? Timestamp.FromDateTimeOffset(setAt) : null,
            Version = state.Version?.ToString("D") ?? ""
        };
}
