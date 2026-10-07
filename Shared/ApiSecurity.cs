using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CsAgent.Shared;

/// <summary>
/// Security rules for the headless API mode (<c>--api</c>). Pure functions, no I/O,
/// so they can be unit-tested without starting a web server.
/// </summary>
public static class ApiSecurity
{
    public const string DefaultHost = "localhost";

    /// <summary>
    /// True when the host only accepts connections from this machine.
    /// "localhost", 127.x.x.x, ::1 and [::1] are loopback; 0.0.0.0, *, + and
    /// any other name or address are considered reachable from the network.
    /// </summary>
    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var h = host.Trim().Trim('[', ']');
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Returns an error message when the combination of host and key is unsafe,
    /// or null when the server may start. Exposing the agent (which can run shell
    /// commands) on the network without a key is refused.
    /// </summary>
    public static string? CheckStartup(string? host, string? apiKey)
    {
        if (!IsLoopbackHost(host) && string.IsNullOrEmpty(apiKey))
            return $"--host {host} makes the agent reachable from the network, and the agent can run commands " +
                   "and write files. Set an API key with --api-key <key> or the CSAGENT_API_KEY environment variable, " +
                   "or keep the default host (localhost).";
        return null;
    }

    /// <summary>
    /// Checks the credentials of a request against the expected key. Accepted forms:
    /// <c>Authorization: Bearer &lt;key&gt;</c> or <c>X-API-Key: &lt;key&gt;</c>.
    /// Always true when no key is configured.
    /// </summary>
    public static bool IsAuthorized(string? expectedKey, string? authorizationHeader, string? apiKeyHeader)
    {
        if (string.IsNullOrEmpty(expectedKey)) return true;

        string? presented = apiKeyHeader;
        if (string.IsNullOrEmpty(presented) && authorizationHeader is not null)
        {
            const string prefix = "Bearer ";
            if (authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                presented = authorizationHeader[prefix.Length..].Trim();
        }
        if (string.IsNullOrEmpty(presented)) return false;

        // Constant-time comparison; hashing first makes the lengths equal.
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
