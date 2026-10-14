using System.Security.Authentication;
using Notifications.Application.Push;

namespace Notifications.Infrastructure.Push;

/// <summary>What both adapters decide alike about an attempt that ended in an exception, not an answer.</summary>
internal static class PushAttempts
{
    /// <summary>A connection never made took nothing; once a request is sent, it may have been delivered.</summary>
    public static PushFault Classify(Exception e, bool sending) => e switch
    {
        HttpRequestException
        {
            HttpRequestError: HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError
                or HttpRequestError.ProxyTunnelError
        } => PushFault.Transient,
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => PushFault.Tls,
        AuthenticationException => PushFault.Tls,
        _ => sending ? PushFault.Unconfirmed : PushFault.Transient
    };

    /// <summary>The type names alone, as an exception's message can quote the request's address or body.</summary>
    public static string Named(Exception e) =>
        e.InnerException is { } inner ? $"{e.GetType().Name} ({inner.GetType().Name})" : e.GetType().Name;

    /// <summary>With a trailing slash, or a relative request would replace the base address's last segment.</summary>
    public static Uri Directory(Uri baseUrl) =>
        baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
}
