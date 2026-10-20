using System.ComponentModel.DataAnnotations;
using Common.Infrastructure.Validation;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§15.4's start-time check: an options class's annotations decide, and each failure's message
/// survives.</summary>
public class AnnotatedOptionsValidatorTests
{
    private sealed class Sample
    {
        [Range(1, 10, ErrorMessage = "Delay is out of range.")]
        public int Delay { get; init; } = 5;

        [Required(ErrorMessage = "Name is required.")]
        public string? Name { get; init; } = "x";
    }

    private static ValidateOptionsResult Validate(Sample options) =>
        new AnnotatedOptionsValidator<Sample>().Validate(name: null, options);

    [Fact]
    public void Valid_options_succeed() => Validate(new Sample()).Succeeded.ShouldBeTrue();

    [Fact]
    public void Every_failed_annotation_is_reported_with_its_message()
    {
        ValidateOptionsResult result = Validate(new Sample { Delay = 0, Name = null });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldBe(["Delay is out of range.", "Name is required."], ignoreOrder: true);
    }
}
