using System.Globalization;
using Common.Contracts.Ordering.V1;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The renderer over the shipped files: language by locale, value by culture, date by zone.</summary>
public class TemplateRendererTests
{
    /// <summary>16:00 in Almaty, and 00:45 the next day in Chatham, thirteen and three-quarter hours ahead.</summary>
    private static readonly DateTimeOffset LateInTheDay = new(2026, 10, 2, 11, 0, 0, TimeSpan.Zero);

    private static readonly NotificationParameters Everything = new()
    {
        OrderId = Guid.Parse("0199a9a0-0000-7000-8000-000000000042"),
        OccurredAt = LateInTheDay,
        Amount = 12345.60m,
        Currency = "KZT",
        TrackingNumber = "KZ-0042",
        CancelReason = CancelReasons.OutOfStock
    };

    private static TemplateRenderer Renderer(string[] languages, string zone = "Asia/Almaty") =>
        TemplateRenderer.Create(TemplateSet.Embedded, languages, zone);

    public static TheoryData<string, string> EveryKeyInEveryLanguage()
    {
        TheoryData<string, string> rows = [];
        foreach (string key in TemplateKeys.Placeholders.Keys)
        {
            foreach (string language in new[] { "en", "kk", "ru" })
                rows.Add(key, language);
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(EveryKeyInEveryLanguage))]
    public void Every_key_renders_in_every_shipped_language_with_every_placeholder_filled(string key, string language)
    {
        RenderedMessage message = Renderer([language]).Render(key, Everything, locale: language);

        message.Subject.ShouldNotBeNullOrWhiteSpace();
        message.Body.ShouldNotContain("{");
        message.Body.ShouldNotContain("}");
        message.Body.ShouldContain(Everything.OrderId.ToString());

        IReadOnlySet<string> placeholders = TemplateKeys.Placeholders[key];
        if (placeholders.Contains(PlaceholderNames.Currency))
            message.Body.ShouldContain("KZT");
        if (placeholders.Contains(PlaceholderNames.TrackingNumber))
            message.Body.ShouldContain("KZ-0042");
        message.TemplateVersion.ShouldBe(1);
        message.Languages.ShouldBe([language]);
    }

    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("ru-RU", "ru")]
    [InlineData("KK", "kk")]
    [InlineData("kk_KZ", "kk")]
    public void A_locale_the_set_holds_is_sent_its_language_alone(string locale, string language)
    {
        RenderedMessage message = Renderer(["kk", "ru", "en"]).Render(TemplateKeys.OrderPlaced, Everything, locale);

        message.Languages.ShouldBe([language]);
        message.Subject.ShouldNotContain(TemplateRenderer.SubjectSeparator);
        message.Body.ShouldNotContain(TemplateRenderer.LanguageRule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    public void Any_other_locale_or_none_is_sent_every_language_of_the_set_in_the_set_s_order(string? locale)
    {
        TemplateRenderer renderer = Renderer(["kk", "en"]);

        RenderedMessage message = renderer.Render(TemplateKeys.OrderConfirmed, Everything, locale);

        message.Languages.ShouldBe(["kk", "en"]);
        message.LanguageList.ShouldBe("kk,en");
        string kazakh = Renderer(["kk"]).Render(TemplateKeys.OrderConfirmed, Everything, "kk").Subject;
        string english = Renderer(["en"]).Render(TemplateKeys.OrderConfirmed, Everything, "en").Subject;
        message.Subject.ShouldBe(kazakh + TemplateRenderer.SubjectSeparator + english);
        message.Body.Split(TemplateRenderer.LanguageRule).Length.ShouldBe(2);
    }

    [Fact]
    public void A_resend_renders_the_stamped_version_and_languages_exactly_as_the_first_send()
    {
        TemplateRenderer renderer = Renderer(["kk", "en"]);
        RenderedMessage first = renderer.Render(TemplateKeys.OrderPlaced, Everything, locale: "en");

        first.Languages.ShouldBe(["en"]);

        RenderedMessage again = renderer.Render(
            TemplateKeys.OrderPlaced,
            Everything,
            first.TemplateVersion,
            first.Languages);

        again.Subject.ShouldBe(first.Subject);
        again.Body.ShouldBe(first.Body);
        again.TemplateVersion.ShouldBe(first.TemplateVersion);
        again.LanguageList.ShouldBe(first.LanguageList);
    }

    [Fact]
    public void A_resend_keeps_the_stamped_languages_where_a_locale_would_now_choose_one()
    {
        // The customer set a locale after the first send; the same Message-ID must carry the same text.
        RenderedMessage again = Renderer(["kk", "en"]).Render(TemplateKeys.OrderConfirmed, Everything, 1, ["en"]);

        again.Languages.ShouldBe(["en"]);
        again.Subject.ShouldNotContain(TemplateRenderer.SubjectSeparator);
    }

    [Fact]
    public void A_resend_of_a_version_the_set_does_not_hold_throws_rather_than_sending_another()
    {
        Should.Throw<InvalidOperationException>(() =>
            Renderer(["kk", "en"]).Render(TemplateKeys.OrderPlaced, Everything, 2, ["kk"]));
    }

    [Fact]
    public void A_resend_in_a_language_the_deployment_dropped_throws_rather_than_rendering_without_a_culture()
    {
        Should
            .Throw<InvalidOperationException>(() =>
                Renderer(["kk", "en"]).Render(TemplateKeys.OrderPlaced, Everything, 1, ["ru"]))
            .Message.ShouldContain("ru");
    }

    [Fact]
    public void A_kazakh_date_and_amount_differ_from_the_invariant_culture_s()
    {
        // Both need ICU and tzdata, which the -chiseled-extra image carries and the plain one does not (§15.2).
        string body = Renderer(["kk"], "Asia/Almaty").Render(TemplateKeys.OrderPlaced, Everything, "kk").Body;

        body.ShouldContain($"12{(char)0x00A0}345,60", Case.Sensitive, "a no-break space groups, a comma separates");
        body.ShouldNotContain(12345.60m.ToString("N2", CultureInfo.InvariantCulture));
        body.ShouldContain("қазан", Case.Sensitive, "October in Kazakh, from the culture's month names");
        body.ShouldNotContain(LateInTheDay.ToString("D", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_date_at_the_edge_of_a_day_lands_on_the_zone_s_side_of_it()
    {
        // 11:00 UTC is 16:00 in Almaty and 00:45 the next morning in Chatham.
        string almaty = Renderer(["en"], "Asia/Almaty").Render(TemplateKeys.OrderPlaced, Everything, "en").Body;
        string chatham = Renderer(["en"], "Pacific/Chatham").Render(TemplateKeys.OrderPlaced, Everything, "en").Body;

        almaty.ShouldContain("October 2, 2026");
        chatham.ShouldContain("October 3, 2026");
        chatham.ShouldNotContain("October 2, 2026");
    }

    [Theory]
    [InlineData("10.125", "10.125")]
    [InlineData("42.10", "42.10")]
    [InlineData("7", "7")]
    [InlineData("1234567.5", "1,234,567.5")]
    public void An_amount_is_shown_at_its_own_scale_and_never_rounded(string amount, string shown)
    {
        decimal value = decimal.Parse(amount, CultureInfo.InvariantCulture);

        string body = Renderer(["en"]).Render(
            TemplateKeys.PaymentRefunded,
            Everything with { Amount = value, Currency = "KWD" },
            "en").Body;

        body.ShouldContain($"{shown} KWD");
    }

    [Fact]
    public void A_value_the_intake_dropped_renders_as_the_absent_mark()
    {
        string body = Renderer(["ru"]).Render(
            TemplateKeys.ShipmentDispatched,
            Everything with { TrackingNumber = null },
            "ru").Body;

        body.ShouldContain($"Трек-номер: {TemplateRenderer.Absent}");
    }

    [Theory]
    [InlineData(CancelReasons.OutOfStock, "Some of the items in it were out of stock.")]
    [InlineData(CancelReasons.StockTimeout, "We could not reserve the items in it in time.")]
    [InlineData(CancelReasons.PaymentDeclined, "The payment for it was not accepted.")]
    [InlineData(CancelReasons.PaymentTimeout, "We did not receive confirmation of the payment in time.")]
    [InlineData(CancelReasons.CustomerRequest, "It was cancelled at your request.")]
    [InlineData("fraud_suspected", "We were unable to complete it.")]
    [InlineData(null, "We were unable to complete it.")]
    public void A_cancellation_says_why_from_its_map_and_an_unknown_code_says_so_generically(
        string? code,
        string phrase)
    {
        string body = Renderer(["en"]).Render(
            TemplateKeys.OrderCancelled,
            Everything with { CancelReason = code },
            "en").Body;

        body.ShouldContain($"has been cancelled. {phrase}");
        if (code is not null)
            body.ShouldNotContain(code);
    }

    [Fact]
    public void An_empty_or_repeated_language_set_is_refused()
    {
        TemplateRenderer
            .Refusals(TemplateSet.Embedded, [], "UTC")
            .ShouldBe(["The language set is empty; ADR-053 makes it a set of at least one."]);
        TemplateRenderer
            .Refusals(TemplateSet.Embedded, ["en", "en"], "UTC")
            .ShouldBe(["The language set names 'en' twice."]);
    }

    [Fact]
    public void A_language_set_too_long_for_the_row_s_languages_column_is_refused()
    {
        string[] languages = ["en", "kk", "ru", "de", "fr", "es", "it", "pt", "nl", "pl", "tr", "uk"];

        TemplateRenderer.Refusals(TemplateSet.Embedded, languages, "UTC").ShouldContain(
            $"The language set joins to 35 characters, past the {NotificationLimits.MaxLanguagesLength} " +
            "a notification's Languages column holds.");
        TemplateRenderer.Refusals(TemplateSet.Embedded, languages[..11], "UTC").ShouldAllBe(
            refusal => !refusal.StartsWith("The language set joins", StringComparison.Ordinal));
    }

    [Fact]
    public void A_language_with_no_templates_is_refused_naming_every_file_it_lacks()
    {
        IReadOnlyList<string> refusals = TemplateRenderer.Refusals(TemplateSet.Embedded, ["en", "de"], "UTC");

        refusals.Count.ShouldBe(TemplateKeys.Placeholders.Count + 1, "seven templates and the cancellation's map");
        refusals.ShouldContain("Templates/order-placed.v1.de.txt is missing, and the language set requires it.");
        refusals.ShouldContain(
            "Templates/order-cancelled.v1.de.reasons.txt is missing, and the language set requires it.");
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Central Asia Standard Time")]
    [InlineData("")]
    public void A_zone_that_is_not_an_iana_id_this_runtime_knows_is_refused(string zone)
    {
        TemplateRenderer
            .Refusals(TemplateSet.Embedded, ["en"], zone)
            .ShouldBe([$"'{zone}' is not an IANA time zone this runtime knows."]);
    }

    [Fact]
    public void Create_refuses_with_every_refusal_at_once()
    {
        TemplateSetException refused = Should.Throw<TemplateSetException>(
            () => TemplateRenderer.Create(TemplateSet.Embedded, ["en", "en"], "Mars/Olympus_Mons"));

        refused.Refusals.Count.ShouldBe(2);
    }

    [Fact]
    public void The_highest_version_present_is_rendered_and_stamped()
    {
        List<TemplateFile> files =
        [
            .. TemplateKeys.Placeholders.Keys.Select(key =>
                new TemplateFile($"Templates/{key}.v1.en.txt", "Subject: One\n\nOrder {OrderId}.\n")),
            new TemplateFile("Templates/order-placed.v2.en.txt", "Subject: Two\n\nOrder {OrderId}, again.\n"),
            new TemplateFile(
                "Templates/order-cancelled.v1.en.reasons.txt",
                string.Concat(
                    TemplateKeys.CancellationCodes.Append(TemplateSet.OtherReason).Select(c => $"{c}: Why.\n")))
        ];

        RenderedMessage message = TemplateRenderer
            .Create(TemplateSet.Parse(files), ["en"], "UTC")
            .Render(TemplateKeys.OrderPlaced, Everything, "en");

        message.TemplateVersion.ShouldBe(2);
        message.Subject.ShouldBe("Two");
    }
}
