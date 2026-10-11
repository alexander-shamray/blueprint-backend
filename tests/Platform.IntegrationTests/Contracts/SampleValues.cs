namespace Platform.IntegrationTests.Contracts;

/// <summary>The values every service's samples draw on, each distinct and non-default (§12.6).</summary>
internal static class SampleValues
{
    internal static readonly Guid Message = new("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid Correlation = new("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid Order = new("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid Customer = new("44444444-4444-4444-4444-444444444444");
    internal static readonly Guid Product = new("55555555-5555-5555-5555-555555555555");
    internal static readonly Guid Request = new("66666666-6666-6666-6666-666666666666");

    // A non-zero offset, so an options change that normalises the offset away is caught.
    internal static readonly DateTimeOffset Occurred =
        new(2026, 8, 11, 9, 30, 0, TimeSpan.FromHours(2));
}
