using Common.Application;

namespace Web.Bff.Addresses;

/// <summary>Every <see cref="Error"/> the saved address returns, constructed here and nowhere else (§10.5).</summary>
public static class SavedAddressErrors
{
    /// <summary>The caller has saved none, or deleted it, or was erased; the three read alike (§10.9).</summary>
    public static readonly Error NotFound = Error.NotFound("address.not_found", "No saved address.");
}
