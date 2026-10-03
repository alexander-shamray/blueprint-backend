using System.Collections.Concurrent;
using Notifications.Application.Contacts;

namespace Notifications.Infrastructure.Delivery;

/// <summary>One send pass's contact reads, one per customer and shared by all their rows (ADR-052).</summary>
/// <remarks>
/// ADR-052 bounds one customer's concurrent reads by the replica count, so a pass holding several of their rows
/// starts one read; a row served a stale contact finds the owner's fault beside it, to say so on its own line.
/// </remarks>
internal sealed class ContactReads
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<(ContactLookup Answer, Exception? Stale)>>> _reads = new();

    /// <summary>The customer's read under way or finished, else <paramref name="read"/> started now.</summary>
    public Task<(ContactLookup Answer, Exception? Stale)> GetOrStart(
        Guid customer,
        Func<Task<(ContactLookup Answer, Exception? Stale)>> read) =>
        _reads.GetOrAdd(customer, _ => new Lazy<Task<(ContactLookup Answer, Exception? Stale)>>(read)).Value;
}
