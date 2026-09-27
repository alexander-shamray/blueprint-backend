namespace Shipping.Domain;

/// <summary>
/// The <c>typeof</c> anchor §4.2's architecture gates need, and nothing else:
/// a gate that reasons about an assembly has to name a type inside it, and
/// this project has none until its first aggregate. Written to be deleted —
/// when that aggregate lands, re-anchor the architecture gates in
/// <c>Shipping.Domain.Tests</c> and <c>Shipping.Application.Tests</c> on it and
/// remove this file, because a marker left in place after the first aggregate
/// means those gates judge an empty type rather than the model.
/// </summary>
public sealed class AssemblyMarker;
