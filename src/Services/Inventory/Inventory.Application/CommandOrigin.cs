namespace Inventory.Application;

/// <summary>§11.4's command origin; Ordering's sits in an assembly §4.3 keeps to itself.</summary>
public enum CommandOrigin
{
    User,
    System
}
