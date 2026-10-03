namespace Notifications.Infrastructure.Delivery;

/// <summary>What one pass did: the rows it claimed, and how many of them reached an outcome.</summary>
public readonly record struct SendPass(int Claimed, int Finished);
