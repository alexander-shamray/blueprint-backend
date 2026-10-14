using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Notifications.Devices.V1;
using Notifications.Preferences.V1;
using Xunit;
using DevicesGrpc = Notifications.Devices.V1.Devices;
using PreferencesGrpc = Notifications.Preferences.V1.Preferences;

namespace Web.Bff.TestSupport;

/// <summary>A real gRPC server on a loopback port, standing in for Notifications.Api on the BFF's hop.</summary>
/// <remarks>Driven through its own surface, as <see cref="StubCatalog"/> is: a test sees no generated type.</remarks>
public sealed class StubNotifications : IAsyncLifetime
{
    private readonly ConcurrentQueue<Call> _calls = new();

    private readonly ConcurrentQueue<PreferenceCall> _preferenceCalls = new();

    private WebApplication? _app;

    /// <summary>The address the BFF's gRPC client is pointed at.</summary>
    public Uri Address { get; private set; } = null!;

    /// <summary>Every call this stub has received, in order, recorded before it is answered.</summary>
    public IReadOnlyCollection<Call> Calls => _calls;

    /// <summary>Answers for the next calls, one per call; with none queued a write is applied.</summary>
    public ConcurrentQueue<Answer> AnswerNextWith { get; } = new();

    /// <summary>Every preferences call this stub has received, in order, recorded before it is answered.</summary>
    public IReadOnlyCollection<PreferenceCall> PreferenceCalls => _preferenceCalls;

    /// <summary>What a preferences read answers.</summary>
    public IReadOnlyList<PreferenceRow> Preferences { get; set; } = [];

    /// <summary>Answers for the next preference writes; with none queued, one is applied anew.</summary>
    public ConcurrentQueue<SetAnswer> AnswerNextSetWith { get; } = new();

    /// <summary>Statuses to fail the next calls with, one per call, before answering normally.</summary>
    public ConcurrentQueue<StatusCode> FailNextWith { get; } = new();

    /// <summary>Calls to answer by aborting the connection, the transport fault §9.7's retry can see.</summary>
    public int AbortNextCalls { get; set; }

    public async ValueTask InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Listen, as ListenLocalhost refuses port 0; Http2 alone, as a cleartext default refuses gRPC's HTTP/2.
        builder.WebHost.ConfigureKestrel(o =>
            o.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(this);

        _app = builder.Build();
        _app.MapGrpcService<StubDevicesService>();
        _app.MapGrpcService<StubPreferencesService>();

        await _app.StartAsync();

        Address = new Uri(_app.Urls.Single());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    /// <summary>The outcomes devices.proto names, and the unset one no reply may send.</summary>
    public enum Outcome { Unspecified, Applied, Repeated, Stale }

    /// <summary>One reply; a null sequence answers with the request's own.</summary>
    public sealed record Answer(Outcome Outcome, long? Sequence = null);

    /// <summary>The sources preferences.proto names, and the unset one no reply may send.</summary>
    public enum Source { Unspecified, Required, Default, Set }

    /// <summary>The write outcomes preferences.proto names, and the unset one no reply may send.</summary>
    public enum SetOutcome { Unspecified, Applied, Repeated, PreconditionFailed, Required }

    /// <summary>One pair as a reply carries it; an empty version and a null time are the wire's unset.</summary>
    public sealed record PreferenceRow(
        string Topic,
        string Channel,
        bool OptedIn,
        Source Source,
        DateTimeOffset? SetAt = null,
        string Version = "");

    /// <summary>One write's reply; a null preference is the wire's unset.</summary>
    public sealed record SetAnswer(SetOutcome Outcome, PreferenceRow? Preference);

    /// <summary>One preferences call as it arrived; a read's pair and value are empty.</summary>
    public sealed record PreferenceCall(
        string Method,
        string Topic,
        string Channel,
        bool OptedIn,
        string ReadVersion,
        string? Authorization,
        string? CorrelationId);

    /// <summary>One call as it arrived; a sign-out's platform, app id and token are empty.</summary>
    public sealed record Call(
        string Method,
        string InstallationId,
        string Platform,
        string AppId,
        string Token,
        long Sequence,
        string? Authorization,
        string? CorrelationId);

    // The fault queues the device stub drains, shared, so a test fails the next call to either service alike.
    private void Fail(ServerCallContext context)
    {
        if (AbortNextCalls > 0)
        {
            AbortNextCalls--;
            context.GetHttpContext().Abort();

            throw new RpcException(new Status(StatusCode.Aborted, "connection aborted"));
        }

        if (FailNextWith.TryDequeue(out StatusCode failure))
            throw new RpcException(new Status(failure, "stubbed failure"));
    }

    private static Preference Wire(PreferenceRow row) =>
        new()
        {
            Topic = row.Topic,
            Channel = row.Channel,
            OptedIn = row.OptedIn,
            Source = (Preference.Types.Source)row.Source,
            SetAt = row.SetAt is { } setAt ? Timestamp.FromDateTimeOffset(setAt) : null,
            Version = row.Version
        };

    private sealed class StubPreferencesService(StubNotifications stub) : PreferencesGrpc.PreferencesBase
    {
        public override Task<PreferencesReply> Get(GetPreferencesRequest request, ServerCallContext context)
        {
            Record(context, nameof(Get), "", "", false, "");
            stub.Fail(context);

            PreferencesReply reply = new();
            reply.Preferences.AddRange(stub.Preferences.Select(Wire));

            return Task.FromResult(reply);
        }

        public override Task<SetPreferenceReply> Set(SetPreferenceRequest request, ServerCallContext context)
        {
            Record(context, nameof(Set), request.Topic, request.Channel, request.OptedIn, request.ReadVersion);
            stub.Fail(context);

            SetAnswer answer = stub.AnswerNextSetWith.TryDequeue(out SetAnswer? queued)
                ? queued
                : new SetAnswer(
                    SetOutcome.Applied,
                    new PreferenceRow(
                        request.Topic,
                        request.Channel,
                        request.OptedIn,
                        Source.Set,
                        DateTimeOffset.UtcNow,
                        Guid.CreateVersion7().ToString("D")));

            return Task.FromResult(
                new SetPreferenceReply
                {
                    Outcome = (SetPreferenceReply.Types.Outcome)answer.Outcome,
                    Preference = answer.Preference is { } row ? Wire(row) : null
                });
        }

        private void Record(
            ServerCallContext context,
            string method,
            string topic,
            string channel,
            bool optedIn,
            string readVersion) =>
            stub._preferenceCalls.Enqueue(
                new PreferenceCall(
                    method,
                    topic,
                    channel,
                    optedIn,
                    readVersion,
                    context.RequestHeaders.GetValue("authorization"),
                    context.RequestHeaders.GetValue("x-correlation-id")));
    }

    private sealed class StubDevicesService(StubNotifications stub) : DevicesGrpc.DevicesBase
    {
        public override Task<DeviceReply> Register(RegisterRequest request, ServerCallContext context) =>
            Answer(
                context,
                nameof(Register),
                request.InstallationId,
                request.Platform,
                request.AppId,
                request.Token,
                request.Sequence);

        public override Task<DeviceReply> SignOut(SignOutRequest request, ServerCallContext context) =>
            Answer(context, nameof(SignOut), request.InstallationId, "", "", "", request.Sequence);

        private Task<DeviceReply> Answer(
            ServerCallContext context,
            string method,
            string installationId,
            string platform,
            string appId,
            string token,
            long sequence)
        {
            stub._calls.Enqueue(
                new Call(
                    method,
                    installationId,
                    platform,
                    appId,
                    token,
                    sequence,
                    context.RequestHeaders.GetValue("authorization"),
                    // Lower-cased, as Metadata is an ordinal list and HTTP/2 names are lower-case on the wire.
                    context.RequestHeaders.GetValue("x-correlation-id")));

            stub.Fail(context);

            Answer answer = stub.AnswerNextWith.TryDequeue(out Answer? queued) ? queued : new(Outcome.Applied);

            return Task.FromResult(
                new DeviceReply
                {
                    Outcome = answer.Outcome switch
                    {
                        Outcome.Applied => DeviceReply.Types.Outcome.Applied,
                        Outcome.Repeated => DeviceReply.Types.Outcome.Repeated,
                        Outcome.Stale => DeviceReply.Types.Outcome.Stale,
                        _ => DeviceReply.Types.Outcome.Unspecified
                    },
                    Sequence = answer.Sequence ?? sequence
                });
        }
    }
}
