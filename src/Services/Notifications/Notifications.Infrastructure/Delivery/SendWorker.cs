using System.Diagnostics;
using Common.Application;
using Common.Infrastructure.Switching;
using Common.Infrastructure.Tracing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Contacts;
using Notifications.Application.Delivery;
using Notifications.Application.Devices;
using Notifications.Application.Mail;
using Notifications.Application.Preferences;
using Notifications.Application.Privacy;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Mail;
using Notifications.Infrastructure.Observability;

namespace Notifications.Infrastructure.Delivery;

/// <summary>Claims notices under a lease: sends a mail notice, and fans a push notice out to its deliveries.</summary>
/// <remarks>
/// <c>FulfilmentWorker</c>'s shape with <c>TrackingWorker</c>'s rows at once. No consumer calls out (ADR-052), and
/// a crash after the relay's accept leaves a row whose intent says so; <see cref="PushSendWorker"/> sends a push.
/// </remarks>
public sealed class SendWorker(
    IServiceScopeFactory scopes,
    ILogger<SendWorker> log,
    IOptions<SendPacingOptions> pacing,
    OperatorSwitches? switches = null) : SwitchedPollingWorker(switches)
{
    /// <summary>The name an operator lists to switch the send pass off (ADR-113).</summary>
    public const string Name = "send-worker";

    /// <summary>Rows one pass sends at once by default; a pass lasts as long as its slowest row.</summary>
    /// <remarks><see cref="SendPacingOptions"/> carries the batch a host runs on (ADR-157).</remarks>
    public const int ClaimBatchSize = 10;

    /// <summary>Above <c>MailHop.TotalTimeout</c> plus <c>ContactHop.TotalRequestTimeout</c>.</summary>
    public const int LeaseSeconds = 45;

    /// <summary>How long a pass under way at a stop runs before its token fires; above one row's calls.</summary>
    public static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(25);

    /// <summary>What a pass under way at a stop leaves of the host's drain for its last commit (§15.3).</summary>
    public static readonly TimeSpan CommitRoom = TimeSpan.FromSeconds(5);

    // CA1848 (ADR-019); a line about a row names it by its ids and never by its mailbox (§13.4).
    private static readonly Action<ILogger, Exception?> ClaimFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(ClaimFailed)),
            "Send claim failed; retrying next tick.");

    private static readonly Action<ILogger, DateTimeOffset?, Exception?> Parked =
        LoggerMessage.Define<DateTimeOffset?>(
            LogLevel.Debug,
            new EventId(2, nameof(Parked)),
            "The relay's breaker is open until {ParkedUntil}; only push notices are claimed.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> PassFailed =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(3, nameof(PassFailed)),
            "Send pass for notification {NotificationId} on order {OrderId} failed; the row backs off.");

    private static readonly Action<ILogger, Guid, Guid, MailFault, Exception?> RelayUnavailable =
        LoggerMessage.Define<Guid, Guid, MailFault>(
            LogLevel.Warning,
            new EventId(4, nameof(RelayUnavailable)),
            "Notification {NotificationId} on order {OrderId} met the relay unavailable ({Cause}); the row backs off.");

    private static readonly Action<ILogger, Guid, Guid, MailFault, Exception?> RelayRefused =
        LoggerMessage.Define<Guid, Guid, MailFault>(
            LogLevel.Error,
            new EventId(5, nameof(RelayRefused)),
            "The relay refused notification {NotificationId} on order {OrderId} as a deployment fault ({Cause}); " +
            "the row backs off until the relay, its session or its sender is fixed.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> ContactRefused =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(6, nameof(ContactRefused)),
            "The contact read for notification {NotificationId} on order {OrderId} was refused over this host's " +
            "credential; the row backs off and no stored contact is served (ADR-052).");

    private static readonly Action<ILogger, Guid, Guid, Exception?> ParametersUnreadable =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(7, nameof(ParametersUnreadable)),
            "Notification {NotificationId} on order {OrderId} stores parameters this version cannot read; the row " +
            "backs off until a version that can claims it.");

    private static readonly Action<ILogger, Guid, Guid, TimeSpan, Exception?> GaveUp =
        LoggerMessage.Define<Guid, Guid, TimeSpan>(
            LogLevel.Warning,
            new EventId(8, nameof(GaveUp)),
            "Notification {NotificationId} on order {OrderId} was pending past its give-up age of {GiveUpAge}; it is " +
            "undeliverable and will not be retried.");

    private static readonly Action<ILogger, Guid, Guid, Guid, Exception?> Suppressed =
        LoggerMessage.Define<Guid, Guid, Guid>(
            LogLevel.Information,
            new EventId(9, nameof(Suppressed)),
            "Notification {NotificationId} on order {OrderId} declined a payment customer {CustomerId} had " +
            "cancelled; it is suppressed (ADR-049).");

    private static readonly Action<ILogger, Guid, Guid, Guid, Exception?> NoSuchCustomer =
        LoggerMessage.Define<Guid, Guid, Guid>(
            LogLevel.Warning,
            new EventId(10, nameof(NoSuchCustomer)),
            "Notification {NotificationId} on order {OrderId} names customer {CustomerId}, for whom the owner gives " +
            "no deliverable contact (unknown, disabled, no email or an unverified one); it is undeliverable and the " +
            "contact row is deleted (ADR-052, ADR-086).");

    private static readonly Action<ILogger, Guid, Guid, Guid, Exception?> ServedStale =
        LoggerMessage.Define<Guid, Guid, Guid>(
            LogLevel.Warning,
            new EventId(11, nameof(ServedStale)),
            "Notification {NotificationId} on order {OrderId} is served customer {CustomerId}'s stored contact, as " +
            "the owner could not answer (ADR-052).");

    private static readonly Action<ILogger, Guid, Guid, string, Exception?> Refused =
        LoggerMessage.Define<Guid, Guid, string>(
            LogLevel.Warning,
            new EventId(12, nameof(Refused)),
            "Notification {NotificationId} on order {OrderId} is undeliverable: {Reason}; it will not be retried.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> Resending =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Warning,
            new EventId(13, nameof(Resending)),
            "Notification {NotificationId} on order {OrderId} was claimed after its intent; it is sent again under " +
            "the same Message-ID.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> SentUncommitted =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(14, nameof(SentUncommitted)),
            "The relay accepted notification {NotificationId} on order {OrderId}, but the outcome was not committed; " +
            "the next pass sends it again under the same Message-ID.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> Outgrown =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Information,
            new EventId(15, nameof(Outgrown)),
            "Notification {NotificationId} on order {OrderId} moved beneath this pass; it is left as it stands.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> BackOffFailed =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(16, nameof(BackOffFailed)),
            "Backoff for notification {NotificationId} on order {OrderId} failed; its lease lapses and a later pass " +
            "reclaims it.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> ErasedCustomer =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Information,
            new EventId(17, nameof(ErasedCustomer)),
            "Notification {NotificationId} on order {OrderId} names an erased customer; nothing is fetched or sent, " +
            "and the erasure deletes the row (ADR-101).");

    private static readonly Action<ILogger, Guid, Guid, Exception?> OptedOut =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Information,
            new EventId(18, nameof(OptedOut)),
            "Notification {NotificationId} on order {OrderId} is suppressed: its customer's newest consent entry " +
            "withdraws it, so nothing is fetched or sent (ADR-120).");

    private static readonly Action<ILogger, Guid, Guid, Guid, Exception?> NoDevice =
        LoggerMessage.Define<Guid, Guid, Guid>(
            LogLevel.Information,
            new EventId(19, nameof(NoDevice)),
            "Push notification {NotificationId} on order {OrderId} finds no device customer {CustomerId} holds; " +
            "it is undeliverable (ADR-117).");

    private static readonly Action<ILogger, Guid, Guid, int, Exception?> Dispatched =
        LoggerMessage.Define<Guid, Guid, int>(
            LogLevel.Information,
            new EventId(20, nameof(Dispatched)),
            "Push notification {NotificationId} on order {OrderId} fanned out to {Devices} device(s).");

    public override string SwitchName => Name;

    protected override TimeSpan PassInterval => pacing.Value.SendTick;

    // A stop starts no claim, and never cuts a claim or rows under way: theirs fires DrainBudget after it.
    protected override TimeSpan? StopDrain => DrainBudget;

    protected override Task RunPassAsync(CancellationToken stoppingToken) =>
        new MailRows(this, scopes, log).RunAsync(stoppingToken, Drain);

    protected override void OnPassFailed(Exception exception) => ClaimFailed(log, exception);

    /// <summary>One claim-and-send pass, public so tests drive it rather than race a timer (§12.4).</summary>
    public Task<SendPass> RunOnceAsync(CancellationToken ct) => new MailRows(this, scopes, log).RunAsync(ct, ct);

    /// <summary>One leased row, step by step; true when it reached an outcome (ADR-049, ADR-052).</summary>
    internal async Task<bool> SendAsync(
        IServiceProvider sp,
        SendClaims claims,
        SendWork work,
        ContactReads reads,
        CancellationToken ct)
    {
        TimeProvider clock = sp.GetRequiredService<TimeProvider>();
        TimeSpan giveUpAge = sp.GetRequiredService<IOptions<DeliveryOptions>>().Value.GiveUpAge!.Value;

        // Asked before anyone is: one age covers every wait, as a notice a day late is worse than none (ADR-052).
        if (SendRules.HasGivenUp(work.CreatedAt, clock.GetUtcNow(), giveUpAge))
        {
            bool ended = await CommitAsync(
                sp,
                work,
                (n, now) => n.MarkUndeliverable(NotificationReasons.GaveUp, now),
                ct);

            if (ended)
                GaveUp(log, work.NotificationId, work.OrderId, giveUpAge, null);

            return ended;
        }

        OrderRecord? order = await sp.GetRequiredService<IOrderRecordRepository>().GetAsync(work.OrderId, ct);

        // §9.4 orders nothing between the events: the row waits on its backoff for Ordering's record (ADR-017).
        if (SendRules.AwaitsOrderRecord(work.TemplateKey, order))
        {
            if (!await claims.BackOffAsync(work.NotificationId, work.LockedUntil, ct))
                Outgrown(log, work.NotificationId, work.OrderId, null);

            return false;
        }

        Guid customer = work.CustomerId ?? order.CustomerId;

        if (SendRules.Suppresses(work.TemplateKey, order))
        {
            bool suppressed = await CommitAsync(
                sp,
                work,
                (n, now) =>
                {
                    n.AssignCustomer(customer);
                    return n.Suppress(now);
                },
                ct);

            if (suppressed)
                Suppressed(log, work.NotificationId, work.OrderId, customer, null);

            return suppressed;
        }

        // The record's earliest event named the customer; a record from before the column asks every row (ADR-139).
        DateTimeOffset sourcedAt = order.SourcedAt ?? DateTimeOffset.MinValue;

        // Before the owner is asked, so no mailbox is fetched for an erased customer; the intake's own check, or the
        // erasure, deletes this row, so it is left to them (ADR-101).
        if (await sp.GetRequiredService<IErasedSubjects>().IsErasedAsync(
            customer,
            order.ErasedUnder,
            sourcedAt,
            ct))
        {
            ErasedCustomer(log, work.NotificationId, work.OrderId, null);
            return false;
        }

        // Copied once, so every later step and §11.7's erasure find the customer on the row.
        if (work.CustomerId is null)
            await CommitAsync(sp, work, (n, _) => n.AssignCustomer(customer), ct);

        if (work.Channel == ConsentVocabulary.Push)
            return await FanOutAsync(sp, work, customer, reads, ct);

        // Decided when the send is made, so a withdrawal reaches the next one; a resend was decided at its intent,
        // which carries the entry, and is not asked again (ADR-120).
        Guid? consentEntry = null;

        if (work.SendStartedAt is null)
        {
            PreferenceState consent =
                await ConsentChecks.StateAsync(sp, customer, work.TemplateKey, ConsentVocabulary.Email, ct);

            if (!consent.OptedIn)
            {
                bool optedOut = await CommitAsync(sp, work, (n, now) => n.OptOut(consent.Version, now), ct);

                if (optedOut)
                    OptedOut(log, work.NotificationId, work.OrderId, null);

                return optedOut;
            }

            consentEntry = consent.Version;
        }

        if (await ContactAsync(sp, work, customer, reads, ct) is not ContactLookup.Found contact)
        {
            bool unknown = await CommitAsync(
                sp,
                work,
                (n, now) => n.MarkUndeliverable(NotificationReasons.NoSuchCustomer, now),
                ct);

            if (unknown)
                NoSuchCustomer(log, work.NotificationId, work.OrderId, customer, null);

            return unknown;
        }

        NotificationParameters parameters = ParametersFormat.Read(work.Parameters);
        TemplateRenderer renderer = sp.GetRequiredService<TemplateRenderer>();
        RenderedMessage message;

        if (work.SendStartedAt is null)
        {
            message = renderer.Render(work.TemplateKey, parameters, contact.Locale);

            // The intent, committed before the send, so a crash after the relay's accept leaves a row that says so.
            bool started = await CommitAsync(
                sp,
                work,
                (n, now) => n.StartSend(message.TemplateVersion, message.LanguageList, consentEntry, now),
                ct);

            if (!started)
            {
                Outgrown(log, work.NotificationId, work.OrderId, null);
                return false;
            }
        }
        else
        {
            // A send that may have reached the relay: the stamped text again, under the same Message-ID, and counted.
            message = renderer.Render(
                work.TemplateKey,
                parameters,
                work.TemplateVersion!.Value,
                work.Languages!.Split(','));
            sp.GetRequiredService<NotificationMetrics>().Resent();
            Resending(log, work.NotificationId, work.OrderId, null);
        }

        MailResult result = await sp.GetRequiredService<IMailChannel>().SendAsync(
            new OutboundMail(
                contact.Email,
                message.Subject,
                message.Body,
                new MailMessageId(work.EventId, work.TemplateKey),
                work.CorrelationId,
                message.Languages),
            ct);

        return result switch
        {
            MailResult.Accepted => await CompleteAsync(sp, work),
            MailResult.Refused refused => await RefusedAsync(sp, work, refused.Reason, ct),
            _ => throw new InvalidOperationException($"Unknown relay answer {result.GetType().Name}.")
        };
    }

    /// <summary>
    /// A push notice's steps past the customer: the pair, the devices, the language, then one transaction that
    /// writes a delivery per device and ends the notice <c>Dispatched</c> (the push design's section 4).
    /// </summary>
    private async Task<bool> FanOutAsync(
        IServiceProvider sp,
        SendWork work,
        Guid customer,
        ContactReads reads,
        CancellationToken ct)
    {
        PreferenceState consent =
            await ConsentChecks.StateAsync(sp, customer, work.TemplateKey, ConsentVocabulary.Push, ct);

        if (!consent.OptedIn)
        {
            bool optedOut = await CommitAsync(sp, work, (n, now) => n.OptOut(consent.Version, now), ct);

            if (optedOut)
                OptedOut(log, work.NotificationId, work.OrderId, null);

            return optedOut;
        }

        IReadOnlyList<PushDevice> devices = await sp.GetRequiredService<PushDevices>().OfSubjectAsync(customer, ct);

        if (devices.Count == 0)
        {
            bool none = await CommitAsync(
                sp,
                work,
                (n, now) => n.MarkUndeliverable(NotificationReasons.NoDevice, now),
                ct);

            if (none)
                NoDevice(log, work.NotificationId, work.OrderId, customer, null);

            return none;
        }

        // For the language alone: a push is addressed to a device, so the mailbox is never used (ADR-052).
        if (await ContactAsync(sp, work, customer, reads, ct) is not ContactLookup.Found contact)
        {
            bool unknown = await CommitAsync(
                sp,
                work,
                (n, now) => n.MarkUndeliverable(NotificationReasons.NoSuchCustomer, now),
                ct);

            if (unknown)
                NoSuchCustomer(log, work.NotificationId, work.OrderId, customer, null);

            return unknown;
        }

        string languages = string.Join(',', sp.GetRequiredService<TemplateRenderer>().LanguagesFor(contact.Locale));
        IPushDeliveryRepository deliveries = sp.GetRequiredService<IPushDeliveryRepository>();

        // One transaction, so a pass repeated after a crash finds the notice pending with no rows, or both done.
        bool dispatched = await CommitAsync(
            sp,
            work,
            (n, now) =>
            {
                if (!n.Dispatch(languages, consent.Version, now))
                    return false;

                foreach (PushDevice device in devices)
                {
                    deliveries.Add(
                        PushDelivery.For(n, device.InstallationId, device.Platform!, consent.Version, languages, now));
                }

                return true;
            },
            ct);

        if (dispatched)
            Dispatched(log, work.NotificationId, work.OrderId, devices.Count, null);
        else
            Outgrown(log, work.NotificationId, work.OrderId, null);

        return dispatched;
    }

    /// <summary>The customer's contact, read once a pass for all their rows (ADR-052).</summary>
    private async Task<ContactLookup> ContactAsync(
        IServiceProvider sp,
        SendWork work,
        Guid customer,
        ContactReads reads,
        CancellationToken ct)
    {
        // The row whose read is kept awaits it in its own scope, so no read outlives the scope it runs in.
        (ContactLookup answer, Exception? stale) =
            await reads.GetOrStart(customer, () => ContactReader.ReadAsync(sp, customer, ct));

        if (stale is not null)
            ServedStale(log, work.NotificationId, work.OrderId, customer, stale);

        return answer;
    }

    private async Task<bool> CompleteAsync(IServiceProvider sp, SendWork work)
    {
        try
        {
            // Never cancelled: the relay holds the message, and an abandoned commit would send it twice. A row already
            // Sent is a retry after a lost acknowledgement (§6.3): the first attempt committed, so it is done.
            return await CommitAsync(
                sp,
                work,
                (n, now) => n.MarkSent(now) || n.Status == NotificationStatus.Sent,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The relay holds the message and the row does not say so; its intent makes the next pass a counted resend.
            SentUncommitted(log, work.NotificationId, work.OrderId, ex);
            throw;
        }
    }

    private async Task<bool> RefusedAsync(
        IServiceProvider sp,
        SendWork work,
        MailRefusal refusal,
        CancellationToken ct)
    {
        string reason = refusal switch
        {
            MailRefusal.RecipientRefused => NotificationReasons.RecipientRefused,
            MailRefusal.NotAMailbox => NotificationReasons.NotAMailbox,
            _ => throw new InvalidOperationException($"Unknown refusal {refusal}.")
        };

        bool ended = await CommitAsync(sp, work, (n, now) => n.MarkUndeliverable(reason, now), ct);

        if (ended)
            Refused(log, work.NotificationId, work.OrderId, reason, null);

        return ended;
    }

    // The type picks the line, never whether the row backs off; none of them quotes a mailbox (§13.4).
    private void Fault(SendWork work, Exception ex)
    {
        switch (ex)
        {
            case MailUnavailableException { Cause: MailFault.Transient or MailFault.Unconfirmed } relay:
                RelayUnavailable(log, work.NotificationId, work.OrderId, relay.Cause, ex);
                break;
            case MailUnavailableException relay:
                RelayRefused(log, work.NotificationId, work.OrderId, relay.Cause, ex);
                break;
            case ContactSourceRefusedException:
                ContactRefused(log, work.NotificationId, work.OrderId, ex);
                break;
            case UnreadableParametersException:
                ParametersUnreadable(log, work.NotificationId, work.OrderId, ex);
                break;
            default:
                PassFailed(log, work.NotificationId, work.OrderId, ex);
                break;
        }
    }

    /// <summary>The mail pass's steps for <see cref="ClaimPass{TWork, TClaims, TState}"/>.</summary>
    private sealed class MailRows(SendWorker worker, IServiceScopeFactory scopes, ILogger log)
        : ClaimPass<SendWork, SendClaims, MailPass>(scopes)
    {
        // An open breaker parks the mail notices, so they keep their backoff and nothing reaches a queue (§9.7), and a
        // push notice still fans out. Past the break a pass claims as usual; its first send probes.
        protected override MailPass Begin(IServiceProvider claimScope)
        {
            MailPipeline relay = claimScope.GetRequiredService<MailPipeline>();
            bool mailParked = relay.IsOpen;
            if (mailParked)
                Parked(log, relay.ParkedUntil, null);

            return new MailPass(mailParked, new ContactReads());
        }

        protected override Task<IReadOnlyList<SendWork>> ClaimAsync(
            SendClaims claims,
            MailPass state,
            CancellationToken rows) => claims.ClaimAsync(state.MailParked, rows);

        // A child of the intake that wrote the row, so the contact read and the relay's send join the order's trace;
        // parent rather than link, as the outbox argues, the wait in the table being real (§9.4).
        protected override Activity? StartSpan(SendWork work)
        {
            Activity? span = new StagedTrace(work.TraceParent, work.TraceState).StartClaimed("notification send");
            span?.SetTag("notifications.notification.id", work.NotificationId);

            return span;
        }

        protected override Task<bool> SendAsync(
            IServiceProvider row,
            SendClaims claims,
            MailPass state,
            SendWork work,
            CancellationToken ct) => worker.SendAsync(row, claims, work, state.Reads, ct);

        protected override Task<bool> BackOffAsync(SendClaims claims, SendWork work, CancellationToken ct) =>
            claims.BackOffAsync(work.NotificationId, work.LockedUntil, ct);

        protected override void LogFault(SendWork work, Exception exception) => worker.Fault(work, exception);

        protected override void LogOutgrown(SendWork work) => Outgrown(log, work.NotificationId, work.OrderId, null);

        protected override void LogBackOffFailed(SendWork work, Exception exception) =>
            BackOffFailed(log, work.NotificationId, work.OrderId, exception);
    }

    private readonly record struct MailPass(bool MailParked, ContactReads Reads);

    /// <summary>One of the record's moves, committed; false when the row had outgrown it and nothing changed.</summary>
    private static Task<bool> CommitAsync(
        IServiceProvider sp,
        SendWork work,
        Func<Notification, DateTimeOffset, bool> move,
        CancellationToken ct) =>
        RowCommit.ExecuteAsync(
            sp,
            async inner =>
                await sp.GetRequiredService<INotificationRepository>().GetAsync(work.NotificationId, inner) ??
                throw new InvalidOperationException(
                    $"Notification {work.NotificationId} was claimed and is now absent."),
            (notification, now, _) => Task.FromResult(move(notification, now)),
            ct);
}
