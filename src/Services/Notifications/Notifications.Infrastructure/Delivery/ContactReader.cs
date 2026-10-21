using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.Application.Delivery;
using Notifications.Application.Privacy;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Delivery;

/// <summary>The contact read a send pass makes for a customer, whether for mail or for a push fan-out
/// (ADR-052).</summary>
internal static class ContactReader
{
    /// <summary>
    /// ADR-052's five outcomes over the stored row and the owner; a stale row is served on any fault but a refusal,
    /// and every other fault throws.
    /// </summary>
    public static async Task<(ContactLookup Answer, Exception? Stale)> ReadAsync(
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
        {
            await store.SaveAsync(customer, found, clock.GetUtcNow(), ct);

            // An erasure since the intake's check finds this row or is seen here, so it is not written back (ADR-100).
            // Asked without an order's requests, since this read serves all the customer's rows (ADR-101), so an order
            // ADR-099 kept loses only a row the next read fetches again; the pass still sends. Asks every row, as
            // the send pass does: no instant of the owner's data is known here (ADR-139).
            if (await sp.GetRequiredService<IErasedSubjects>().IsErasedAsync(
                customer,
                [],
                DateTimeOffset.MinValue,
                ct))
                await store.DeleteAsync(customer, ct);
        }
        else
        {
            await store.DeleteAsync(customer, ct);
        }

        return (answer, null);
    }
}
