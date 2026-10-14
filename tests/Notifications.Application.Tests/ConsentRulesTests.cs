using Notifications.Application.Preferences;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>ADR-120's decision, one row per source, and the vocabulary and pair text it is keyed by.</summary>
public class ConsentRulesTests
{
    private static readonly DateTimeOffset Written = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly ConsentPair Dispatched = new(TemplateKeys.ShipmentDispatched, ConsentVocabulary.Push);

    private static ConsentEntry Entry(bool optedIn) =>
        new(Guid.CreateVersion7(), Dispatched.Topic, Dispatched.Channel, optedIn, Written, "commerce-app", null);

    [Fact]
    public void A_pair_with_no_entry_takes_its_service_default_which_is_on()
    {
        PreferenceState state = ConsentRules.StateOf(Dispatched, new RequiredPairs([]), newest: null);

        state.ShouldBe(
            new PreferenceState(Dispatched.Topic, Dispatched.Channel, true, PreferenceSource.Default, null, null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_newest_entry_decides_and_its_id_is_the_version(bool optedIn)
    {
        ConsentEntry newest = Entry(optedIn);

        PreferenceState state = ConsentRules.StateOf(Dispatched, new RequiredPairs([]), newest);

        state.ShouldBe(
            new PreferenceState(
                Dispatched.Topic,
                Dispatched.Channel,
                optedIn,
                PreferenceSource.Set,
                Written,
                newest.EntryId));
    }

    [Fact]
    public void A_required_pair_is_on_whatever_the_journal_holds_and_shows_no_entry()
    {
        // A withdrawal written before the deployment required the pair cannot switch off the duty (ADR-120).
        PreferenceState state = ConsentRules.StateOf(Dispatched, new RequiredPairs([Dispatched]), Entry(false));

        state.ShouldBe(
            new PreferenceState(Dispatched.Topic, Dispatched.Channel, true, PreferenceSource.Required, null, null));
    }

    [Fact]
    public void The_vocabulary_is_the_seven_order_events_on_two_channels_each_a_service_topic()
    {
        ConsentVocabulary.Topics.ShouldBe(TemplateKeys.Placeholders.Keys, ignoreOrder: true);
        ConsentVocabulary.Channels.ShouldBe(["email", "push"]);
        ConsentVocabulary.Topics.ShouldAllBe(topic => ConsentVocabulary.IsService(topic));
    }

    [Theory]
    [InlineData("order-placed/email", true)]
    [InlineData("shipment-delivered/push", true)]
    [InlineData("order-placed", false)]
    [InlineData("order-placed/sms", false)]
    [InlineData("newsletter/email", false)]
    [InlineData("order-placed/email/extra", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_pair_parses_from_its_text_only_inside_the_vocabulary(string? text, bool parses)
    {
        ConsentPair.TryParse(text, out ConsentPair? pair).ShouldBe(parses);

        if (parses)
            pair!.Value.ToString().ShouldBe(text);
    }
}
