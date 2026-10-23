using System.ComponentModel.DataAnnotations;
using System.Reflection;
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
    public void Validating_writes_nothing_to_the_attribute_because_every_field_is_readonly()
    {
        // RangeAttribute fails at random because its first use converts its string bounds and writes them back to
        // a shared instance. A field that cannot be written after construction cannot race.
        FieldInfo[] written = [.. typeof(TimeSpanRangeAttribute)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => !field.IsInitOnly)
            .Where(field => field.DeclaringType == typeof(TimeSpanRangeAttribute))];

        written.ShouldBeEmpty("a field written after the constructor is the race this attribute exists to remove");
    }

    [Fact]
    public void No_options_type_in_src_bounds_a_time_span_with_a_range_attribute()
    {
        // The form this attribute replaces; the floor below keeps the scan from reading nothing.
        string source = Path.Combine(RepositoryRoot(), "src");
        string[] sources = [.. Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)];

        sources.ShouldContain(path => path.EndsWith("TimeSpanRangeAttribute.cs", StringComparison.Ordinal));
        sources
            .Where(path => File.ReadAllText(path).Contains("Range(typeof(TimeSpan", StringComparison.Ordinal))
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
