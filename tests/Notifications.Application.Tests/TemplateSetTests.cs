using Common.Contracts.Ordering.V1;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The shipped files, and each refusal that fails the host at start, naming the file (ADR-053).</summary>
public class TemplateSetTests
{
    private static readonly string[] Shipped = ["en", "kk", "ru"];

    /// <summary>A minimal valid set in one language, which each refusal test breaks in one place.</summary>
    private static List<TemplateFile> Minimal(string language = "en") =>
    [
        .. TemplateKeys.Placeholders.Keys.Select(key =>
            new TemplateFile($"Templates/{key}.v1.{language}.txt", "Subject: A notice\n\nOrder {OrderId}.\n")),
        new TemplateFile(
            $"Templates/order-cancelled.v1.{language}.reasons.txt",
            string.Concat(TemplateKeys.CancellationCodes.Append(TemplateSet.OtherReason).Select(c => $"{c}: Why.\n")))
    ];

    private static List<TemplateFile> With(List<TemplateFile> files, TemplateFile replacement)
    {
        files.RemoveAll(f => f.Name == replacement.Name);
        files.Add(replacement);
        return files;
    }

    [Fact]
    public void The_shipped_set_is_seven_keys_in_three_languages_at_version_one()
    {
        TemplateSet shipped = TemplateSet.Embedded;

        shipped.Languages.ShouldBe(Shipped, ignoreOrder: true);
        foreach (string key in TemplateKeys.Placeholders.Keys)
        {
            shipped.CurrentVersion(key).ShouldBe(1, key);
            foreach (string language in Shipped)
                shipped.Find(key, 1, language).ShouldNotBeNull($"{key} in {language}");
        }

        shipped.MissingFor(Shipped).ShouldBeEmpty();
    }

    [Fact]
    public void The_assembly_holds_twenty_four_resources_under_the_folder_none_split_into_a_satellite()
    {
        int resources = typeof(TemplateSet).Assembly.GetManifestResourceNames()
            .Count(name => name.StartsWith(TemplateSet.Folder, StringComparison.Ordinal));

        resources.ShouldBe(24, "twenty-one templates and three maps, none split into a satellite");
    }

    [Fact]
    public void Every_shipped_cancellation_map_phrases_each_code_and_the_generic_case()
    {
        foreach (string language in Shipped)
        {
            IReadOnlyDictionary<string, string> reasons = TemplateSet.Embedded.Reasons(1, language).ShouldNotBeNull();

            reasons.Keys.ShouldBe(
                [
                    CancelReasons.OutOfStock,
                    CancelReasons.StockTimeout,
                    CancelReasons.PaymentDeclined,
                    CancelReasons.PaymentTimeout,
                    CancelReasons.CustomerRequest,
                    TemplateSet.OtherReason
                ],
                ignoreOrder: true);
        }
    }

    [Fact]
    public void Every_shipped_template_is_about_the_customer_s_order_and_links_nowhere()
    {
        // ADR-053 rule 4 is the review's to judge; what a test can hold is that each names the order and no link.
        foreach (string key in TemplateKeys.Placeholders.Keys)
        {
            foreach (string language in Shipped)
            {
                Template template = TemplateSet.Embedded.Find(key, 1, language)!;
                string body = string.Concat(template.Body.Select(p => p.IsPlaceholder ? $"{{{p.Text}}}" : p.Text));

                body.ShouldContain("{OrderId}", Case.Sensitive, $"{key} in {language}");
                foreach (string link in new[] { "http:", "https:", "www.", "://" })
                    (template.Subject + body).ShouldNotContain(link, Case.Insensitive, $"{key} in {language}");
            }
        }
    }

    [Fact]
    public void A_placeholder_outside_its_key_s_set_is_refused_naming_the_file()
    {
        List<TemplateFile> files = With(
            Minimal(),
            new TemplateFile("Templates/payment-declined.v1.en.txt", "Subject: No\n\nOrder {OrderId} {Amount}.\n"));

        TemplateSetException refused = Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files));

        refused.Refusals.ShouldHaveSingleItem().ShouldBe(
            "Templates/payment-declined.v1.en.txt: names {Amount}, " +
            "which is not among payment-declined's placeholders.");
    }

    [Fact]
    public void A_subject_naming_a_placeholder_is_refused()
    {
        List<TemplateFile> files = With(
            Minimal(),
            new TemplateFile("Templates/order-placed.v1.en.txt", "Subject: Order {OrderId}\n\nOrder {OrderId}.\n"));

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem()
            .ShouldBe(
                "Templates/order-placed.v1.en.txt: its subject names a placeholder, and a subject may name none.");
    }

    [Theory]
    [InlineData("Order {OrderId")]
    [InlineData("Order OrderId}")]
    [InlineData("Order {}")]
    [InlineData("Order {Order Id}")]
    [InlineData("Order {OrderId.ToString()}")]
    public void A_brace_that_opens_no_placeholder_is_refused(string body)
    {
        List<TemplateFile> files = With(
            Minimal(),
            new TemplateFile("Templates/order-placed.v1.en.txt", $"Subject: Placed\n\n{body}\n"));

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem().ShouldStartWith("Templates/order-placed.v1.en.txt: the brace at offset");
    }

    [Theory]
    [InlineData("Order {OrderId}.\n")]
    [InlineData("subject: Placed\n\nOrder {OrderId}.\n")]
    [InlineData("Subject: \n\nOrder {OrderId}.\n")]
    public void A_first_line_that_is_not_a_subject_is_refused(string text)
    {
        List<TemplateFile> files = With(Minimal(), new TemplateFile("Templates/order-placed.v1.en.txt", text));

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem()
            .ShouldBe("Templates/order-placed.v1.en.txt: its first line is not 'Subject: ' and a subject.");
    }

    [Theory]
    [InlineData("Templates/order-placed.en.txt")]
    [InlineData("Templates/order-placed.v01.en.txt")]
    [InlineData("Templates/order-placed.v1.EN.txt")]
    [InlineData("Templates/order-placed.v1.english.txt")]
    [InlineData("order-placed.v1.en.txt")]
    public void A_name_not_of_the_form_is_refused(string name)
    {
        List<TemplateFile> files = [.. Minimal(), new TemplateFile(name, "Subject: Placed\n\nOrder {OrderId}.\n")];

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem().ShouldStartWith($"{name}: not Templates/");
    }

    [Theory]
    [InlineData("Templates/order-shipped.v1.en.txt")]
    [InlineData("Templates/order-placed.v1.en.reasons.txt")]
    public void A_file_no_key_takes_is_refused(string name)
    {
        List<TemplateFile> files = [.. Minimal(), new TemplateFile(name, "Subject: Shipped\n\nOrder {OrderId}.\n")];

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem().ShouldBe($"{name}: names no template key that takes this file.");
    }

    [Fact]
    public void A_key_with_no_template_at_all_is_refused()
    {
        List<TemplateFile> files = Minimal();
        files.RemoveAll(f => f.Name == "Templates/shipment-delivered.v1.en.txt");

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem()
            .ShouldBe("Templates/shipment-delivered: no template is shipped for this key at any version.");
    }

    [Fact]
    public void A_cancellation_map_missing_a_code_or_naming_an_unknown_one_is_refused()
    {
        List<TemplateFile> files = With(
            Minimal(),
            new TemplateFile(
                "Templates/order-cancelled.v1.en.reasons.txt",
                "out_of_stock: Gone.\nstock_timeout: Late.\npayment_declined: No.\npayment_timeout: Slow.\n" +
                "fraud: Suspect.\n*: Why.\n"));

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files)).Refusals.ShouldBe(
        [
            "Templates/order-cancelled.v1.en.reasons.txt: 'fraud' is not a CancelReasons code or '*'.",
            "Templates/order-cancelled.v1.en.reasons.txt: 'customer_request' has no phrase."
        ]);
    }

    [Fact]
    public void The_highest_version_present_is_the_current_one_and_a_language_without_it_is_missing()
    {
        // A new version is a new file beside the old (ADR-053 rule 4), so v1 stays for the rows that name it.
        List<TemplateFile> files =
        [
            .. Minimal("en"),
            .. Minimal("kk"),
            new TemplateFile("Templates/order-placed.v2.en.txt", "Subject: Placed again\n\nOrder {OrderId}.\n")
        ];

        TemplateSet set = TemplateSet.Parse(files);

        set.CurrentVersion(TemplateKeys.OrderPlaced).ShouldBe(2);
        set.Find(TemplateKeys.OrderPlaced, 1, "en").ShouldNotBeNull();
        set.MissingFor(["en"]).ShouldBeEmpty();
        set.MissingFor(["en", "kk"]).ShouldBe(["Templates/order-placed.v2.kk.txt"]);
    }

    [Fact]
    public void A_required_language_with_no_cancellation_map_is_missing_its_map()
    {
        List<TemplateFile> files = Minimal();
        files.RemoveAll(f => f.Name.EndsWith(".reasons.txt", StringComparison.Ordinal));

        TemplateSet.Parse(files).MissingFor(["en"]).ShouldBe(["Templates/order-cancelled.v1.en.reasons.txt"]);
    }

    [Fact]
    public void A_checkout_s_line_endings_change_nothing_a_customer_reads()
    {
        // A Windows checkout may arrive CRLF; the body a customer reads is the same either way.
        TemplateSet lf = TemplateSet.Parse(Minimal());
        TemplateSet crlf = TemplateSet.Parse(
            [.. Minimal().Select(f => f with { Text = f.Text.Replace("\n", "\r\n", StringComparison.Ordinal) })]);

        Template a = lf.Find(TemplateKeys.OrderPlaced, 1, "en")!;
        Template b = crlf.Find(TemplateKeys.OrderPlaced, 1, "en")!;

        b.Subject.ShouldBe(a.Subject);
        b.Body.ShouldBe(a.Body);
    }

    [Fact]
    public void Every_refusal_in_a_set_is_reported_together()
    {
        List<TemplateFile> files = With(
            With(
                Minimal(),
                new TemplateFile("Templates/order-placed.v1.en.txt", "Subject: {OrderId}\n\nOrder {OrderId}.\n")),
            new TemplateFile("Templates/payment-declined.v1.en.txt", "Subject: No\n\n{Amount}\n"));

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files)).Refusals.Count.ShouldBe(2);
    }
}
