using System.Diagnostics;
using Common.Application;
using Common.Infrastructure.Switching;
using Common.Infrastructure.Tracing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Delivery;
using Notifications.Application.Devices;
using Notifications.Application.Preferences;
using Notifications.Application.Push;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Mail;
using Notifications.Infrastructure.Push;

namespace Notifications.Infrastructure.Delivery;

/// <summary>Claims push deliveries under a lease and calls the push port, each delivery ended alone.</summary>
/// <remarks>
/// A loop of its own beside <see cref="SendWorker"/>, as its waits are a provider's: the relay's breaker never stops
/// a push, and one provider's open breaker parks its own platform's rows alone (the push design's section 1).
/// </remarks>
public sealed class PushSendWorker(
    IServiceScopeFactory scopes,
    ILogger<PushSendWorker> log,
    OperatorSwitches? switches = null) : SwitchedPollingWorker(switches)
{
    /// <summary>The name an operator lists to switch the push pass off (ADR-113).</summary>
    public const string Name = "push-send-worker";

    /// <summary>Rows one pass sends at once; a pass lasts as long as its slowest row.</summary>
    public const int ClaimBatchSize = 10;

    /// <summary>Above the larger of <c>FcmHop.TotalTimeout</c> and <c>ApnsHop.TotalTimeout</c>.</summary>
    public const int LeaseSeconds = 30;

    /// <summary>How often the pass claims, the send worker's pace, so a fan-out waits a tick for its send.</summary>
    public static readonly TimeSpan Tick = MailHop.SendTick;

    /// <summary>How long a pass under way at a stop runs before its token fires; above one row's call.</summary>
    public static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(25);

    /// <summary>What a pass under way at a stop leaves of the host's drain for its last commit (§15.3).</summary>
    public static readonly TimeSpan CommitRoom = TimeSpan.FromSeconds(5);

    // CA1848 (ADR-019); a line about a row names it by its ids and never by its token (ADR-117).
    private static readonly Action<ILogger, Exception?> ClaimFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(ClaimFailed)),
            "Push claim failed; retrying next tick.");

    private static readonly Action<ILogger, string, DateTimeOffset?, Exception?> Parked =
        LoggerMessage.Define<string, DateTimeOffset?>(
            LogLevel.Debug,
            new EventId(2, nameof(Parked)),
            "The {Platform} provider's breaker is open until {ParkedUntil}; its deliveries are not claimed.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> PassFailed =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(3, nameof(PassFailed)),
            "Push pass for delivery {DeliveryId} of notification {NotificationId} failed; the row backs off.");

    private static readonly Action<ILogger, Guid, string, PushFault, Exception?> ProviderUnavailable =
        LoggerMessage.Define<Guid, string, PushFault>(
            LogLevel.Warning,
            new EventId(4, nameof(ProviderUnavailable)),
            "Delivery {DeliveryId} met the {Platform} provider unavailable ({Cause}); the row backs off.");

    private static readonly Action<ILogger, Guid, string, PushFault, Exception?> ProviderRefusedDeployment =
        LoggerMessage.Define<Guid, string, PushFault>(
            LogLevel.Error,
            new EventId(5, nameof(ProviderRefusedDeployment)),
            "Delivery {DeliveryId} was refused by the {Platform} provider as a deployment fault ({Cause}); the row " +
            "backs off until its credential or payload is fixed.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> ParametersUnreadable =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(6, nameof(ParametersUnreadable)),
            "Delivery {DeliveryId} of notification {NotificationId} renders parameters this version cannot read; the " +
            "row backs off until a version that can claims it.");

    private static readonly Action<ILogger, Guid, TimeSpan, Exception?> GaveUp =
        LoggerMessage.Define<Guid, TimeSpan>(
            LogLevel.Warning,
            new EventId(7, nameof(GaveUp)),
            "Delivery {DeliveryId} was pending past its notice's give-up age of {GiveUpAge}; it is undeliverable.");

    private static readonly Action<ILogger, Guid, Exception?> OptedOut =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(8, nameof(OptedOut)),
            "Delivery {DeliveryId} is suppressed: its customer's newest consent entry withdraws push (ADR-120).");

    private static readonly Action<ILogger, Guid, Guid, Exception?> NoDevice =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Information,
            new EventId(9, nameof(NoDevice)),
            "Delivery {DeliveryId} to installation {InstallationId} finds the device gone, cleared or another " +
            "subject's; it is undeliverable and nothing is sent (ADR-117).");

    private static readonly Action<ILogger, Guid, string, Exception?> Refused =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(10, nameof(Refused)),
            "Delivery {DeliveryId} is undeliverable: {Reason}; it will not be retried.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> DeviceRemoved =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Information,
            new EventId(11, nameof(DeviceRemoved)),
            "Delivery {DeliveryId}'s unregistered answer deleted installation {InstallationId}'s row (ADR-117).");

    private static readonly Action<ILogger, Guid, Exception?> Resending =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(12, nameof(Resending)),
            "Delivery {DeliveryId} was claimed after its intent; it is sent again under the same collapse key.");

    private static readonly Action<ILogger, Guid, Exception?> SentUncommitted =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(13, nameof(SentUncommitted)),
            "The provider accepted delivery {DeliveryId}, but the outcome was not committed; the next pass sends it " +
            "again under the same collapse key.");

    private static readonly Action<ILogger, Guid, Exception?> Outgrown =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(14, nameof(Outgrown)),
            "Delivery {DeliveryId} moved beneath this pass; it is left as it stands.");

    private static readonly Action<ILogger, Guid, Exception?> BackOffFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(15, nameof(BackOffFailed)),
            "Backoff for delivery {DeliveryId} failed; its lease lapses and a later pass reclaims it.");

    public override string SwitchName => Name;

    protected override TimeSpan PassInterval => Tick;

    // A stop starts no claim, and never cuts rows under way: theirs fires DrainBudget after it.
    protected override TimeSpan? StopDrain => DrainBudget;

    protected override Task RunPassAsync(CancellationToken stoppingToken) => RunOnceAsync(stoppingToken, Drain);

    protected override void OnPassFailed(Exception exception) => ClaimFailed(log, exception);

    /// <summary>One claim-and-send pass, public so tests drive it rather than race a timer (§12.4).</summary>
    public Task<SendPass> RunOnceAsync(CancellationToken ct) => RunOnceAsync(ct, ct);

    // The claim runs on the drain's token, for SendWorker's reason: a read cancelled after its lease commits strands
    // the rows until the lease lapses (§15.3).
    private async Task<SendPass> RunOnceAsync(CancellationToken claim, CancellationToken rows)
    {
        await using AsyncServiceScope claimScope = scopes.CreateAsyncScope();

        // An open breaker parks its platform's rows, so they keep their backoff (§9.7); past the break a pass claims
        // them again, and its first send probes.
        List<string> parked = [];
        foreach (IPushProvider provider in claimScope.ServiceProvider.GetServices<IPushProvider>())
        {
            if (!provider.Pipeline.IsOpen)
                continue;

            parked.Add(provider.Platform);
            Parked(log, provider.Platform, provider.Pipeline.ParkedUntil, null);
        }

        PushClaims claims = claimScope.ServiceProvider.GetRequiredService<PushClaims>();
        claim.ThrowIfCancellationRequested();
        IReadOnlyList<PushWork> claimed = await claims.ClaimAsync(parked, rows);

        // Every row at once, so a pass lasts one row's call and the lease bounds it.
        bool[] finished = await Task.WhenAll(claimed.Select(work => SendOrBackOffAsync(claims, work, rows)));

        return new SendPass(claimed.Count, finished.Count(f => f));
    }

    private async Task<bool> SendOrBackOffAsync(PushClaims claims, PushWork work, CancellationToken ct)
    {
        // A child of the fan-out that wrote the row, so the provider's call joins the order's trace (§9.4).
        using Activity? span = new StagedTrace(work.TraceParent, work.TraceState).StartClaimed("push send");
        span?.SetTag("notifications.delivery.id", work.DeliveryId);

        try
        {
            // A scope per row, so a row that throws mid-write hands the next none of its tracked state.
            await using AsyncServiceScope row = scopes.CreateAsyncScope();

            return await SendAsync(row.ServiceProvider, work, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            span?.SetStatus(ActivityStatusCode.Error);

            // Logged before the backoff is written, so a database fault there cannot hide this one.
            Fault(work, ex);

            try
            {
                if (!await claims.BackOffAsync(work.DeliveryId, work.LockedUntil, ct))
                    Outgrown(log, work.DeliveryId, null);
            }
            catch (Exception backOff) when (!ct.IsCancellationRequested)
            {
                BackOffFailed(log, work.DeliveryId, backOff);
            }

            return false;
        }
    }

    /// <summary>One leased delivery, step by step; true when it reached an outcome (the push design).</summary>
    internal async Task<bool> SendAsync(IServiceProvider sp, PushWork work, CancellationToken ct)
    {
        TimeProvider clock = sp.GetRequiredService<TimeProvider>();
        TimeSpan giveUpAge = sp.GetRequiredService<IOptions<DeliveryOptions>>().Value.GiveUpAge!.Value;

        // The cascade deletes a delivery with its notice, so a claimed row's notice is there.
        Notification notice =
            await sp.GetRequiredService<INotificationRepository>().GetAsync(work.NotificationId, ct) ??
            throw new InvalidOperationException($"Delivery {work.DeliveryId}'s notification is absent.");

        Guid customer = work.CustomerId ??
            throw new InvalidOperationException($"Delivery {work.DeliveryId} names no customer.");

        // From the notice's creation, so a push is never later than its mail would have been allowed to be.
        if (SendRules.HasGivenUp(notice.CreatedAt, clock.GetUtcNow(), giveUpAge))
        {
            bool ended = await CommitAsync(
                sp,
                work,
                (d, now) => d.MarkUndeliverable(NotificationReasons.GaveUp, now),
                ct);

            if (ended)
                GaveUp(log, work.DeliveryId, giveUpAge, null);

            return ended;
        }

        // Asked again at the send, as a delivery may wait out hours of backoff; a resend was decided at its intent.
        Guid? consentEntry = work.ConsentEntryId;

        if (work.SendStartedAt is null)
        {
            PreferenceState consent = await SendWorker.ConsentAsync(
                sp,
                customer,
                notice.TemplateKey,
                ConsentVocabulary.Push,
                ct);

            if (!consent.OptedIn)
            {
                bool optedOut = await CommitAsync(sp, work, (d, now) => d.OptOut(consent.Version, now), ct);

                if (optedOut)
                    OptedOut(log, work.DeliveryId, null);

                return optedOut;
            }

            consentEntry = consent.Version;
        }

        PushDevices devices = sp.GetRequiredService<PushDevices>();
        PushDevice? device = await devices.GetAsync(work.InstallationId, ct);

        // A device moved to another subject since the fan-out must never show this one's notice (ADR-117).
        if (!SendRules.Reaches(device, customer, work.Platform))
        {
            bool gone = await CommitAsync(
                sp,
                work,
                (d, now) => d.MarkUndeliverable(NotificationReasons.NoDevice, now),
                ct);

            if (gone)
                NoDevice(log, work.DeliveryId, work.InstallationId, null);

            return gone;
        }

        NotificationParameters parameters = ParametersFormat.Read(notice.Parameters);
        TemplateRenderer renderer = sp.GetRequiredService<TemplateRenderer>();
        string[] languages = work.Languages!.Split(',');
        RenderedMessage message;

        if (work.SendStartedAt is null)
        {
            message = renderer.RenderPush(notice.TemplateKey, parameters, languages);

            // The intent, committed before the call, so a crash after the provider's accept leaves a row saying so.
            if (!await CommitAsync(sp, work, (d, now) => d.StartSend(message.TemplateVersion, consentEntry, now), ct))
            {
                Outgrown(log, work.DeliveryId, null);
                return false;
            }
        }
        else
        {
            // A push that may have reached the device: the stamped text again, under the same collapse key, counted.
            message = renderer.RenderPush(notice.TemplateKey, parameters, work.TemplateVersion!.Value, languages);
            sp.GetRequiredService<PushMetrics>().Resent();
            Resending(log, work.DeliveryId, null);
        }

        PushResult result = await sp.GetRequiredService<IPushChannel>().SendAsync(
            new OutboundPush(
                device!.Platform!,
                device.AppId!,
                device.Token!,
                message.Subject,
                message.Body,
                notice.OrderId,
                notice.TemplateKey,
                notice.EventId.ToString("N"),
                work.DeliveryId,
                work.CorrelationId,
                notice.CreatedAt + giveUpAge),
            ct);

        return result switch
        {
            PushResult.Accepted => await CompleteAsync(sp, work),
            PushResult.Refused refused => await RefusedAsync(sp, work, device, refused.Reason, ct),
            _ => throw new InvalidOperationException($"Unknown provider answer {result.GetType().Name}.")
        };
    }

    private async Task<bool> CompleteAsync(IServiceProvider sp, PushWork work)
    {
        try
        {
            // Never cancelled: the provider holds the push, and an abandoned commit would send it twice. A row already
            // Sent is a retry after a lost acknowledgement (§6.3).
            return await CommitAsync(
                sp,
                work,
                (d, now) => d.MarkSent(now) || d.Status == NotificationStatus.Sent,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            SentUncommitted(log, work.DeliveryId, ex);
            throw;
        }
    }

    // Both refusals end the delivery; only an unregistered one deletes the row, and only while it holds the token the
    // send used, in the outcome's transaction, so a token rotated under the send keeps its row (ADR-117, ADR-121).
    private async Task<bool> RefusedAsync(
        IServiceProvider sp,
        PushWork work,
        PushDevice device,
        PushRefusal refusal,
        CancellationToken ct)
    {
        string reason = refusal switch
        {
            PushRefusal.Unregistered => NotificationReasons.Unregistered,
            PushRefusal.InvalidToken => NotificationReasons.InvalidToken,
            _ => throw new InvalidOperationException($"Unknown refusal {refusal}.")
        };

        PushDevices devices = sp.GetRequiredService<PushDevices>();
        int removed = 0;

        bool ended = await CommitAsync(
            sp,
            work,
            async (d, now, inner) =>
            {
                removed = 0;
                if (!d.MarkUndeliverable(reason, now))
                    return false;

                if (refusal == PushRefusal.Unregistered)
                    removed = await devices.DeleteHoldingAsync(device.InstallationId, device.Token!, inner);

                return true;
            },
            ct);

        if (!ended)
            return false;

        Refused(log, work.DeliveryId, reason, null);
        if (removed > 0)
        {
            sp.GetRequiredService<PushMetrics>().DevicesRemoved(PushMetrics.Unregistered, removed);
            DeviceRemoved(log, work.DeliveryId, work.InstallationId, null);
        }

        return true;
    }

    // The type picks the line, never whether the row backs off; none of them quotes a token (ADR-117).
    private void Fault(PushWork work, Exception ex)
    {
        switch (ex)
        {
            case PushUnavailableException { Cause: PushFault.Transient or PushFault.Unconfirmed } provider:
                ProviderUnavailable(log, work.DeliveryId, work.Platform, provider.Cause, ex);
                break;
            case PushUnavailableException provider:
                ProviderRefusedDeployment(log, work.DeliveryId, work.Platform, provider.Cause, ex);
                break;
            case UnreadableParametersException:
                ParametersUnreadable(log, work.DeliveryId, work.NotificationId, ex);
                break;
            default:
                PassFailed(log, work.DeliveryId, work.NotificationId, ex);
                break;
        }
    }

    private static Task<bool> CommitAsync(
        IServiceProvider sp,
        PushWork work,
        Func<PushDelivery, DateTimeOffset, bool> move,
        CancellationToken ct) =>
        CommitAsync(sp, work, (d, now, _) => Task.FromResult(move(d, now)), ct);

    /// <summary>One of the delivery's moves, committed; false when the row outgrew it or an erasure took it.</summary>
    private static async Task<bool> CommitAsync(
        IServiceProvider sp,
        PushWork work,
        Func<PushDelivery, DateTimeOffset, CancellationToken, Task<bool>> move,
        CancellationToken ct)
    {
        IUnitOfWork unitOfWork = sp.GetRequiredService<IUnitOfWork>();

        return await unitOfWork.ExecuteAsync(
            async inner =>
            {
                // An erasure deletes a pending delivery under the pass, which then has nothing to move (§11.7).
                PushDelivery? delivery =
                    await sp.GetRequiredService<IPushDeliveryRepository>().GetAsync(work.DeliveryId, inner);

                if (delivery is null)
                    return false;

                bool moved = await move(delivery, sp.GetRequiredService<TimeProvider>().GetUtcNow(), inner);
                await unitOfWork.SaveChangesAsync(inner);

                return moved;
            },
            ct);
    }
}
