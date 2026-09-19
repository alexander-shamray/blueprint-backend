namespace Inventory.Application;

/// <summary>
/// §9.5's origin for a command, a literal at the call site: <c>User</c> at
/// an endpoint, <c>System</c> in a message mapper. Inventory's own, because
/// Ordering's is inside a service assembly §4.3 keeps to itself.
/// </summary>
public enum CommandOrigin
{
    User,
    System
}
