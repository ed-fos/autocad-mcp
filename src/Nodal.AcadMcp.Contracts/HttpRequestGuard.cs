using System.Security.Cryptography;
using System.Text;

namespace Nodal.AcadMcp.Contracts;

/// <summary>
/// Header parsing and admission control for the in-AutoCAD bridge.
///
/// This lives in Contracts, away from any AutoCAD dependency, for one reason: it is the
/// security boundary in front of run_csharp and run_lisp, so it has to be unit-testable
/// without launching AutoCAD. See Nodal.AcadMcp.Tests for the cases it is held to.
/// </summary>
public static class HttpRequestGuard
{
    /// <summary>
    /// Parses a request head into a method, path and case-insensitive header map.
    /// Returns false for anything that is not a plausible HTTP/1.1 request line.
    /// </summary>
    public static bool TryParseHead(
        string headerText,
        out string method,
        out string path,
        out Dictionary<string, string> headers)
    {
        method = string.Empty;
        path = string.Empty;
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return false;

        method = parts[0];
        try { path = Uri.UnescapeDataString(parts[1]); }
        catch (UriFormatException) { return false; }

        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            // Last value wins, which matches how a duplicated header would be treated
            // downstream; what matters is that a smuggled second Host cannot be ignored.
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return true;
    }

    /// <summary>
    /// Decides whether a parsed request may be dispatched. Returns null to allow, or the HTTP
    /// status to refuse with.
    ///
    /// The four checks, in order of what they actually buy:
    ///   1. TOKEN — the real boundary. Stops any other local process, not just a browser.
    ///   2. HOST — must be loopback and our own port. This is what defeats DNS rebinding,
    ///      where an attacker domain resolves to 127.0.0.1 and would otherwise be same-origin.
    ///   3. ORIGIN/REFERER — a browser sets one on cross-origin requests; a real MCP client
    ///      never sends either, so presence alone is disqualifying.
    ///   4. CONTENT-TYPE — requiring application/json makes the request non-CORS-simple, so a
    ///      browser must preflight, and the preflight fails because no CORS headers are served.
    /// </summary>
    public static int? Authorize(
        string method,
        IReadOnlyDictionary<string, string> headers,
        string expectedToken,
        int port)
    {
        if (string.IsNullOrEmpty(expectedToken)) return 403;

        if (!headers.TryGetValue(AcadMcpProtocol.TokenHeader, out var supplied) ||
            string.IsNullOrEmpty(supplied)) return 403;

        if (!FixedTimeEquals(supplied, expectedToken)) return 403;

        if (!headers.TryGetValue("Host", out var host) || string.IsNullOrEmpty(host)) return 403;
        if (!host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)) return 403;

        if (headers.ContainsKey("Origin") || headers.ContainsKey("Referer")) return 403;

        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            if (!headers.TryGetValue("Content-Type", out var contentType) ||
                !contentType.TrimStart().StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                return 415;
        }

        return null;
    }

    /// <summary>
    /// Compares in time independent of how many leading characters match, so the token cannot
    /// be recovered byte by byte. Length is compared first and separately, which leaks only the
    /// length — the token is fixed-length, so that is not a secret.
    /// </summary>
    public static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }
}
