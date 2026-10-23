using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Common.Infrastructure.Validation;

/// <summary>A <see cref="TimeSpan"/> between two inclusive bounds, parsed once when the attribute is built.</summary>
/// <remarks>
/// <see cref="RangeAttribute"/> converts its string bounds on first use and writes them back to the attribute, which
/// is shared per property, so two hosts first validating one options type at once can cast a bound already converted.
/// The bounds here are fields set in the constructor and never written again.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class TimeSpanRangeAttribute : ValidationAttribute
{
    private readonly TimeSpan _minimum;
    private readonly TimeSpan _maximum;

    /// <summary>Both bounds as <see cref="TimeSpan"/> constant strings, read in the invariant culture.</summary>
    public TimeSpanRangeAttribute(string minimum, string maximum)
        : base("The field {0} must be between {1} and {2}.")
    {
        _minimum = TimeSpan.Parse(minimum, CultureInfo.InvariantCulture);
        _maximum = TimeSpan.Parse(maximum, CultureInfo.InvariantCulture);

        if (_minimum > _maximum)
            throw new ArgumentException($"The minimum {minimum} is above the maximum {maximum}.", nameof(minimum));
    }

    /// <summary>Null is valid, as for <see cref="RangeAttribute"/>: presence is <c>[Required]</c>'s to judge.</summary>
    public override bool IsValid(object? value) =>
        value is null || (value is TimeSpan span && span >= _minimum && span <= _maximum);

    public override string FormatErrorMessage(string name) =>
        string.Format(CultureInfo.CurrentCulture, ErrorMessageString, name, _minimum, _maximum);
}
