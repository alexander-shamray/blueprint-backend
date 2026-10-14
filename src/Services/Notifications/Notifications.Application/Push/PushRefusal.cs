namespace Notifications.Application.Push;

/// <summary>Why a delivery is over for good, each a terminal reason of its own on the row.</summary>
public enum PushRefusal
{
    /// <summary>The token is valid for no app any more, the one answer ADR-117 deletes the row on.</summary>
    Unregistered,

    /// <summary>The provider will not deliver this token for this sender, and ADR-121 keeps the row.</summary>
    InvalidToken
}
