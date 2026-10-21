using System.Net;

namespace CsAgent.Shared;

/// <summary>
/// The one place where the app's <see cref="HttpClient"/>s are made, so they all behave the same behind a
/// company proxy. Behind a proxy that asks for Windows authentication (HTTP 407), the signed-in user's
/// credentials are handed to the proxy. They go to the proxy only: <c>UseDefaultCredentials</c> would send
/// them to whatever server asks for them, so it is not used.
/// </summary>
public static class HttpClients
{
    /// <summary>A new client with this timeout. The caller disposes it, as before.</summary>
    public static HttpClient Create(TimeSpan timeout) => new(NewHandler()) { Timeout = timeout };

    /// <summary>The handler behind <see cref="Create"/> (public so a test can look at it).</summary>
    public static HttpClientHandler NewHandler() => new() { DefaultProxyCredentials = CredentialCache.DefaultCredentials };
}
