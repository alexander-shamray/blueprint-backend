namespace Inventory.Application;

/// <summary>
/// §9.5's two mappings of a command's origin, both literals: <c>User</c> at
/// an endpoint, <c>System</c> in a message mapper. Inventory's own, because
/// Ordering's is inside a service assembly §4.3 keeps to itself.
/// </summary>
public enum CommandOrigin
{
    User,
    System
}
