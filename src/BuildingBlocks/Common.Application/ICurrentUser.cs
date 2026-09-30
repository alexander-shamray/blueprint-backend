namespace Common.Application;

/// <summary>The caller behind the operation, as a port, so no handler sees <c>HttpContext</c> (§11.4).</summary>
public interface ICurrentUser
{
    /// <summary>False for an anonymous request and for a message-borne command alike.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Throws, rather than returning <see cref="Guid.Empty"/>, when there is no subject.</summary>
    Guid Id { get; }

    /// <summary>Reads the claim the endpoint policies read (§11.4).</summary>
    bool HasPermission(string permission);
}
