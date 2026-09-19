namespace Payments.Domain;

/// <summary>
/// The <c>typeof</c> anchor §4.2's architecture gates need, and nothing else.
/// A gate that reasons about an assembly has to name a type inside it, and
/// this project has none until its first aggregate.
/// </summary>
/// <remarks>
/// Written to be deleted: re-anchor <c>ArchitectureTests</c> on the first
/// aggregate and remove this file, so the gates judge the model they exist
/// to constrain rather than an empty type.
/// </remarks>
public sealed class AssemblyMarker;
