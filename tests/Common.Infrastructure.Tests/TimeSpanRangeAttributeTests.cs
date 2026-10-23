using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.RegularExpressions;
using Common.Infrastructure.Validation;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>A <c>TimeSpan</c> bound that holds under two hosts validating one options type at once (§15.4).</summary>
public class TimeSpanRangeAttributeTests
{
    private sealed class Sample
    {
        [TimeSpanRange("00:00:01", "01:00:00")]
        public TimeSpan? Wait { get; init; }
    }

    private static List<ValidationResult> Validate(TimeSpan? wait)
    {
        Sample options = new() { Wait = wait };
        List<ValidationResult> failures = [];

        Validator.TryValidateObject(options, new ValidationContext(options), failures, validateAllProperties: true);

        return failures;
    }

    [Theory]
    [InlineData("00:00:01")]
    [InlineData("00:30:00")]
    [InlineData("01:00:00")]
    public void A_value_within_the_bounds_or_on_one_is_valid(string value) =>
        Validate(TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ShouldBeEmpty();

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:00:00.9999999")]
    [InlineData("01:00:00.0000001")]
    [InlineData("-00:30:00")]
    public void A_value_outside_the_bounds_is_refused_naming_the_property_and_both_bounds(string value)
    {
        ValidationResult failure = Validate(TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldHaveSingleItem();

        failure.ErrorMessage.ShouldNotBeNull().ShouldContain(nameof(Sample.Wait));
        failure.ErrorMessage.ShouldContain("00:00:01");
        failure.ErrorMessage.ShouldContain("01:00:00");
    }

    [Fact]
    public void An_absent_value_is_left_to_required() => Validate(null).ShouldBeEmpty();

    [Theory]
    [InlineData("soon", "01:00:00")]
    [InlineData("00:00:01", "later")]
    public void A_bound_that_is_not_a_time_span_fails_when_the_attribute_is_built(string minimum, string maximum) =>
        Should.Throw<FormatException>(() => new TimeSpanRangeAttribute(minimum, maximum));

    [Fact]
    public void A_minimum_above_the_maximum_fails_when_the_attribute_is_built() =>
        Should.Throw<ArgumentException>(() => new TimeSpanRangeAttribute("02:00:00", "01:00:00"));

    [Fact]
    public void Validating_writes_nothing_to_the_attribute_because_no_field_it_declares_is_writable()
    {
        // RangeAttribute fails at random because its first use converts its string bounds and writes them back to
        // its own fields, which a subclass inherits. ValidationAttribute's own fields are trusted, not checked.
        FieldInfo[] written = [.. typeof(TimeSpanRangeAttribute)
            .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => !field.IsInitOnly && !field.IsLiteral)
            .Where(field => field.DeclaringType == typeof(TimeSpanRangeAttribute))];

        written.ShouldBeEmpty("a field written after the constructor is the race this attribute exists to remove");
        typeof(TimeSpanRangeAttribute).BaseType.ShouldBe(
            typeof(ValidationAttribute),
            "a base class that converts and caches would move the state this test reads out of reach");
    }

    // The stock attribute with a TimeSpan operand: Range or RangeAttribute, spaced, System- or global-qualified, the
    // operand named or not.
    private static readonly Regex StockTimeSpanRange = new(
        @"\bRange(?:Attribute)?\s*\(\s*(?:type\s*:\s*)?typeof\s*\(\s*(?:global\s*::\s*)?(?:System\s*\.\s*)?TimeSpan\s*\)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData("[Range(typeof(TimeSpan), \"00:00:01\", \"01:00:00\")]")]
    [InlineData("[Range(typeof(TimeSpan), Min, Max, ParseLimitsInInvariantCulture = true)]")]
    [InlineData("[Range( typeof( System.TimeSpan ), Min, Max)]")]
    [InlineData("[RangeAttribute(typeof(TimeSpan), Min, Max)]")]
    [InlineData("[Required, Range(\n typeof(TimeSpan), Min, Max)]")]
    [InlineData("[Range(typeof(global::System.TimeSpan), Min, Max)]")]
    [InlineData("[Range(type: typeof(TimeSpan), minimum: Min, maximum: Max)]")]
    public void The_scan_below_matches_every_spelling_it_hunts(string declaration) =>
        StockTimeSpanRange.IsMatch(declaration).ShouldBeTrue();

    [Theory]
    [InlineData("[TimeSpanRange(Min, Max)]")]
    [InlineData("[Range(1, 65535)]")]
    [InlineData("[Range(typeof(decimal), \"0\", \"1\")]")]
    public void The_scan_below_leaves_alone_what_it_does_not_hunt(string declaration) =>
        StockTimeSpanRange.IsMatch(declaration).ShouldBeFalse();

    [Fact]
    public void No_options_type_in_src_bounds_a_time_span_with_a_range_attribute()
    {
        // The form this attribute replaces; the floor below keeps the scan from reading nothing.
        string source = Path.Combine(RepositoryRoot(), "src");
        string[] sources = [.. Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)];

        sources.ShouldContain(path => path.EndsWith("TimeSpanRangeAttribute.cs", StringComparison.Ordinal));
        sources
            .Where(path => StockTimeSpanRange.IsMatch(File.ReadAllText(path)))
            .ShouldBeEmpty("a string-bounded RangeAttribute on a TimeSpan is the attribute that races on first use");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Platform.slnx")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException($"No Platform.slnx above '{AppContext.BaseDirectory}'.");
    }
}
