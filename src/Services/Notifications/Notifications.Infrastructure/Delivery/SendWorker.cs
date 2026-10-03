using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Contacts;
using Notifications.Application.Delivery;
using Notifications.Application.Mail;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Mail;
using Notifications.Infrastructure.Observability;

namespace Notifications.Infrastructure.Delivery;

/// <summary>Claims notices under a lease and makes every call that leaves the service.</summary>
/// <remarks>
/// <c>FulfilmentWorker</c>'s shape with <c>TrackingWorker</c>'s rows at once. No consumer calls out (ADR-052), and
/// the intent is committed before the send, so a crash after the relay's accept is a row that says so.
/// </remarks>
public sealed class SendWorker(
    IServiceScopeFactory scopes,
    ILogger<SendWorker> log) : BackgroundService
{
    /// <summary>Rows one pass sends at once; a pass lasts as long as its slowest row.</summary>
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
            "The relay's breaker is open until {ParkedUntil}; no notice is claimed.");

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
            "Notification {NotificationId} on order {OrderId} names customer {CustomerId}, whom the owner does not " +
            "know; it is undeliverable and the contact row is deleted (ADR-052).");

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

    // stoppingToken, not ct: CA1725 keeps the base's name, an error under ADR-019.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(MailHop.SendTick);

        // A stop ends the loop and a claim in flight, never the rows under way: theirs fires DrainBudget after it.
        using CancellationTokenSource drain = new();
        using CancellationTokenRegistration stopping = stoppingToken.Register(() => drain.CancelAfter(DrainBudget));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken, drain.Token);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The token, not the type: a call's own deadline throws the same type, and an escape stops the host.
                ClaimFailed(log, ex);
            }
        }
    }

    /// <summary>One claim-and-send pass, public so tests drive it rather than race a timer (§12.4).</summary>
    public Task<SendPass> RunOnceAsync(CancellationToken ct) => RunOnceAsync(ct, ct);

    // The claim on the stop's token, so a stop leases and starts no row; the rows on the drain's (§15.3).
    private async Task<SendPass> RunOnceAsync(CancellationToken claim, CancellationToken rows)
    {
        await using AsyncServiceScope claimScope = scopes.CreateAsyncScope();
        MailPipeline relay = claimScope.ServiceProvider.GetRequiredService<MailPipeline>();

        // The breaker parks the claim, not the messages: rows keep their backoff and nothing reaches a queue (§9.7).
        if (relay.IsOpen)
        {
            Parked(log, relay.ParkedUntil, null);
            return new SendPass(0, 0);
        }

        SendClaims claims = claimScope.ServiceProvider.GetRequiredService<SendClaims>();
        IReadOnlyList<SendWork> claimed = await claims.ClaimAsync(claim);

        // Every row at once, so a pass lasts one row's calls and the lease bounds it; WhenAll, so one row's fault
        // leaves the others to finish.
        ContactReads reads = new();
        bool[] finished = await Task.WhenAll(claimed.Select(work => SendOrBackOffAsync(claims, work, reads, rows)));

        return new SendPass(claimed.Count, finished.Count(f => f));
    }

    private async Task<bool> SendOrBackOffAsync(
        SendClaims claims,
        SendWork work,
        ContactReads reads,
        CancellationToken ct)
    {
        try
        {
            // A scope per row, so a row that throws mid-write hands the next none of its tracked state.
            await using AsyncServiceScope row = scopes.CreateAsyncScope();

            return await SendAsync(row.ServiceProvider, claims, work, reads, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Logged before the backoff is written, so a database fault there cannot hide this one.
            Fault(work, ex);

            try
            {
                await claims.BackOffAsync(work.NotificationId, ct);
            }
            catch (Exception backOff) when (!ct.IsCancellationRequested)
            {
                // The claim held, so the loop's line would be false; the lease lapses and a later pass reclaims it.
                BackOffFailed(log, work.NotificationId, work.OrderId, backOff);
            }

            return false;
        }
    }

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
                sp, work, (n, now) => n.MarkUndeliverable(NotificationReasons.GaveUp, now), ct);

            if (ended)
                GaveUp(log, work.NotificationId, work.OrderId, giveUpAge, null);

            return ended;
        }

        OrderRecord? order = await sp.GetRequiredService<IOrderRecordRepository>().GetAsync(work.OrderId, ct);

        // §9.4 orders nothing between the events: the row waits on its backoff for Ordering's record (ADR-017).
        if (SendRules.AwaitsOrderRecord(work.TemplateKey, order))
        {
            await claims.BackOffAsync(work.NotificationId, ct);
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

        // Copied once, so every later step and §11.7's erasure find the customer on the row.
        if (work.CustomerId is null)
            await CommitAsync(sp, work, (n, _) => n.AssignCustomer(customer), ct);

        if (await ContactAsync(sp, work, customer, reads, ct) is not ContactLookup.Found contact)
        {
            bool unknown = await CommitAsync(
                sp, work, (n, now) => n.MarkUndeliverable(NotificationReasons.NoSuchCustomer, now), ct);

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
                sp, work, (n, now) => n.StartSend(message.TemplateVersion, message.LanguageList, now), ct);

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
                work.TemplateKey, parameters, work.TemplateVersion!.Value, work.Languages!.Split(','));
            sp.GetRequiredService<NotificationMetrics>().Resent();
            Resending(log, work.NotificationId, work.OrderId, null);
        }

        MailResult result = await sp.GetRequiredService<IMailChannel>().SendAsync(
            new OutboundMail(
                contact.Email,
                message.Subject,
                message.Body,
                new MailMessageId(work.EventId, work.TemplateKey),
                message.Languages),
            ct);

        return result switch
        {
            MailResult.Accepted => await CompleteAsync(sp, work),
            MailResult.Refused refused => await RefusedAsync(sp, work, refused.Reason, ct),
            _ => throw new InvalidOperationException($"Unknown relay answer {result.GetType().Name}.")
        };
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
            await reads.GetOrStart(customer, () => ReadContactAsync(sp, customer, ct));

        if (stale is not null)
            ServedStale(log, work.NotificationId, work.OrderId, customer, stale);

        return answer;
    }

    /// <summary>
    /// ADR-052's five outcomes over the stored row and the owner; a stale row is served on any fault but a refusal,
    /// and every other fault throws.
    /// </summary>
    private static async Task<(ContactLookup Answer, Exception? Stale)> ReadContactAsync(
        IServiceProvider sp,
        Guid customer,
        CancellationToken ct)
    {
        IContactStore store = sp.GetRequiredService<IContactStore>();
        TimeProvider clock = sp.GetRequiredService<TimeProvider>();

        ContactRecord? stored = await store.GetAsync(customer, ct);
        ContactAge age = SendRules.AgeOf(stored, clock.GetUtcNow(), sp.GetRequiredService<ContactOptions>());

        if (age == ContactAge.Fresh)
            return (new ContactLookup.Found(stored!.Email, stored.Locale), null);

        ContactLookup answer;

        try
        {
            answer = await sp.GetRequiredService<IContactSource>().GetAsync(customer, ct);
        }
        // An owner that cannot answer is served a stale row; a refused credential never is (ADR-052).
        catch (Exception ex) when (
            ex is not ContactSourceRefusedException && age == ContactAge.Stale && !ct.IsCancellationRequested)
        {
            return (new ContactLookup.Found(stored!.Email, stored.Locale), ex);
        }

        // Kept before the send, so a pass repeated after a crash reads it from the row and not the owner (ADR-052).
        if (answer is ContactLookup.Found found)
            await store.SaveAsync(customer, found, clock.GetUtcNow(), ct);
        else
            await store.DeleteAsync(customer, ct);

        return (answer, null);
    }

    private async Task<bool> CompleteAsync(IServiceProvider sp, SendWork work)
    {
        try
        {
            // Never cancelled: the relay holds the message, and an abandoned commit would send it twice.
            return await CommitAsync(sp, work, (n, now) => n.MarkSent(now), CancellationToken.None);
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

    /// <summary>One of the record's moves, committed; false when the row had outgrown it and nothing changed.</summary>
    private static async Task<bool> CommitAsync(
        IServiceProvider sp,
        SendWork work,
        Func<Notification, DateTimeOffset, bool> move,
        CancellationToken ct)
    {
        IUnitOfWork unitOfWork = sp.GetRequiredService<IUnitOfWork>();

        return await unitOfWork.ExecuteAsync(
            async inner =>
            {
                Notification notification =
                    await sp.GetRequiredService<INotificationRepository>().GetAsync(work.NotificationId, inner)
                    ?? throw new InvalidOperationException(
                        $"Notification {work.NotificationId} was claimed and is now absent.");

                bool moved = move(notification, sp.GetRequiredService<TimeProvider>().GetUtcNow());
                await unitOfWork.SaveChangesAsync(inner);

                return moved;
            },
            ct);
    }
}
