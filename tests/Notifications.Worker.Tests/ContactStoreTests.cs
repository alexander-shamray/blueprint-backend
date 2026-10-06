using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-052's contact row over a real engine, as every claim here is the column's (§12.4).</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ContactStoreTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>An internationalised mailbox, in the letters a Cyrillic code page would lose.</summary>
    private static readonly ContactLookup.Found Kazakh = new("айгерім@мысал.қаз", "kk");

    private static readonly DateTimeOffset Fetched = new(2026, 10, 2, 9, 30, 0, TimeSpan.FromHours(5));

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_kazakh_script_mailbox_and_its_locale_round_trip_with_the_instant_they_were_fetched()
    {
        Guid customer = Guid.CreateVersion7();

        await SaveAsync(customer, Kazakh, Fetched);

        (await ReadAsync(customer)).ShouldBe(new ContactRecord(Kazakh.Email, Kazakh.Locale, Fetched));
    }

    [Fact]
    public async Task A_second_save_for_one_customer_leaves_one_row_holding_the_later_answer()
    {
        Guid customer = Guid.CreateVersion7();
        ContactLookup.Found later = new("aigerim@example.test", null);
        await SaveAsync(customer, Kazakh, Fetched);

        await SaveAsync(customer, later, Fetched.AddMinutes(20));

        (await CountAsync(customer)).ShouldBe(1);
        (await ReadAsync(customer)).ShouldBe(new ContactRecord(later.Email, null, Fetched.AddMinutes(20)));
    }

    [Fact]
    public async Task Concurrent_saves_for_one_customer_leave_one_row_and_no_fault()
    {
        // ADR-052 accepts a burst once per replica, so two first saves of one customer can race.
        Guid customer = Guid.CreateVersion7();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            SaveAsync(customer, new ContactLookup.Found($"customer{i}@example.test", null), Fetched.AddSeconds(i))));

        (await CountAsync(customer)).ShouldBe(1);
    }

    [Fact]
    public async Task An_absent_locale_reads_back_as_absent()
    {
        Guid customer = Guid.CreateVersion7();

        await SaveAsync(customer, Kazakh with { Locale = null }, Fetched);

        (await ReadAsync(customer))!.Locale.ShouldBeNull();
    }

    [Fact]
    public async Task A_customer_with_no_row_reads_as_null()
    {
        (await ReadAsync(Guid.CreateVersion7())).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_removes_the_row_and_deleting_an_absent_one_is_no_fault()
    {
        Guid customer = Guid.CreateVersion7();
        await SaveAsync(customer, Kazakh, Fetched);

        await DeleteAsync(customer);
        await DeleteAsync(customer);

        (await ReadAsync(customer)).ShouldBeNull();
    }

    [Fact]
    public async Task A_mailbox_carrying_a_line_break_is_stored_as_it_arrived()
    {
        // The mail channel refuses it as no mailbox (MailRefusal.NotAMailbox); a store that cleaned it would hide that.
        Guid customer = Guid.CreateVersion7();
        ContactLookup.Found broken = new("aigerim@example.test\r\nbcc: someone@example.test", null);

        await SaveAsync(customer, broken, Fetched);

        (await ReadAsync(customer))!.Email.ShouldBe(broken.Email);
    }

    [Fact]
    public async Task A_mailbox_at_the_owners_own_width_is_stored_whole()
    {
        Guid customer = Guid.CreateVersion7();
        string widest = new string('a', ContactLimits.MaxEmailLength - "@example.test".Length) + "@example.test";

        await SaveAsync(customer, new ContactLookup.Found(widest, null), Fetched);

        (await ReadAsync(customer))!.Email.ShouldBe(widest);
    }

    [Fact]
    public async Task The_columns_are_the_ones_the_record_names_and_nothing_else_the_owner_offered()
    {
        string[] columns = await fixture.ColumnsAsync("notifications", "ContactRecords");

        columns.ShouldBe(
            ["CustomerId", "Email", "Locale", "FetchedAt"],
            ignoreOrder: true,
            "ADR-052: the mailbox, the locale and the instant, and nothing else Keycloak offered");
    }

    private Task<int> CountAsync(Guid customer) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.ContactRecords WHERE CustomerId = {0}",
            customer);

    private async Task SaveAsync(Guid customer, ContactLookup.Found contact, DateTimeOffset fetchedAt)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<IContactStore>()
            .SaveAsync(customer, contact, fetchedAt, TestContext.Current.CancellationToken);
    }

    private async Task<ContactRecord?> ReadAsync(Guid customer)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<IContactStore>()
            .GetAsync(customer, TestContext.Current.CancellationToken);
    }

    private async Task DeleteAsync(Guid customer)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<IContactStore>()
            .DeleteAsync(customer, TestContext.Current.CancellationToken);
    }
}
