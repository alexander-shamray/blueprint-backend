using Common.Contracts.Ordering.V1;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-053 rule 2: all seven notices render under the fixture's invented languages and zone.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class MadeUpDeploymentTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>16:00 in Almaty and 00:45 the next morning in the fixture's Chatham.</summary>
    private static readonly DateTimeOffset LateInTheDay = new(2026, 10, 2, 11, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetWithRelayAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Every_notice_renders_in_the_invented_languages_in_their_order_and_in_the_invented_zone()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Confirmed(order, customer, LateInTheDay));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, LateInTheDay, CancelReasons.OutOfStock, CancelOrigins.Workflow));
        await fixture.DeliverAsync(OrderEvents.Declined(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Refunded(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, LateInTheDay));

        TemplateRenderer renderer = fixture.Factory.Services.GetRequiredService<TemplateRenderer>();
        IReadOnlyList<Notification> owed = await fixture.NotificationsAsync(order);
        owed.Count.ShouldBe(7);

        foreach (Notification notice in owed)
        {
            // No locale, which is what a contact without one gets: every language of the set (ADR-053).
            RenderedMessage message =
                renderer.Render(notice.TemplateKey, ParametersFormat.Read(notice.Parameters), locale: null);

            message.Languages.ShouldBe(NotificationsWorkerFactory.InventedLanguages, notice.TemplateKey);
            message.Subject.Split(TemplateRenderer.SubjectSeparator).Length.ShouldBe(2, notice.TemplateKey);

            string[] bodies = message.Body.Split(TemplateRenderer.LanguageRule);
            bodies.Length.ShouldBe(2, notice.TemplateKey);
            bodies[0].ShouldContain("3 қазан", Case.Sensitive, $"{notice.TemplateKey}: Kazakh first, in Chatham's day");
            bodies[1].ShouldContain("October 3, 2026", Case.Sensitive, $"{notice.TemplateKey}: English second");
            bodies[1].ShouldNotContain("October 2", Case.Sensitive, "UTC's day, which the zone has already left");
        }
    }

    [Fact]
    public async Task All_seven_notices_send_under_the_invented_deployment_in_its_languages_and_its_zone()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        // No locale, so each is owed every language of the set, in the set's order (ADR-053).
        fixture.ContactAnswers(customer, "aigerim@example.test");

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Confirmed(order, customer, LateInTheDay));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, LateInTheDay, CancelReasons.OutOfStock, CancelOrigins.Workflow));
        await fixture.DeliverAsync(OrderEvents.Declined(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Refunded(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, LateInTheDay));

        await fixture.SendUntilSettledAsync();

        IReadOnlyList<Notification> rows = await fixture.NotificationsAsync(order);
        rows.Count.ShouldBe(7, "the workflow's cancellation sends the decline beside it (ADR-049)");
        rows.ShouldAllBe(n => n.Status == NotificationStatus.Sent && n.Languages == "kk,en");

        foreach (MailpitSummary summary in await fixture.Relay.WaitForAsync(7, ct))
        {
            summary.Subject.Split(TemplateRenderer.SubjectSeparator).Length.ShouldBe(2, summary.MessageId);
            (await fixture.Relay.HeadersAsync(summary.Id, ct))["Content-Language"]
                .ShouldHaveSingleItem().ShouldBe("kk, en", summary.MessageId);

            string[] bodies = (await fixture.Relay.MessageAsync(summary.Id, ct)).Text
                .ReplaceLineEndings("\n")
                .Split(TemplateRenderer.LanguageRule);
            bodies.Length.ShouldBe(2, summary.MessageId);
            bodies[0].ShouldContain("3 қазан", Case.Sensitive, $"{summary.MessageId}: Kazakh first, Chatham's day");
            bodies[1].ShouldContain("October 3, 2026", Case.Sensitive, $"{summary.MessageId}: English second");
        }
    }
}
