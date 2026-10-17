using System.Globalization;
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
    /// Decides, from the request head alone, whether to read the body and dispatch or to refuse
    /// without reading it.
    ///
    /// Authorize runs first and its answer wins. An unauthenticated caller gets the same terse
    /// refusal whatever it sent, so it learns nothing, not even that a body limit exists. Only a
    /// caller that has shown it holds the token is told its body is too large or its
    /// Content-Length is malformed. That is also what makes a large body cheap to refuse: nothing
    /// is allocated for it, it is only drained.
    /// </summary>
    public static Admission Admit(
        string method,
        IReadOnlyDictionary<string, string> headers,
        string expectedToken,
        int port,
        int maxBodyBytes)
    {
        var denial = Authorize(method, headers, expectedToken, port);
        if (denial is not null)
            return Admission.Refuse(denial.Value, ToolEnvelope.Failure("forbidden", "Request rejected."));

        long length = 0;
        if (headers.TryGetValue("Content-Length", out var declared) &&
            !long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out length))
        {
            // Parsing this as zero used to dispatch the call with no arguments and leave the real
            // body unread on the socket.
            return Admission.Refuse(400, ToolEnvelope.Failure(
                "bad_request",
                "Content-Length must be a non-negative whole number of bytes.",
                $"Got '{declared}'."));
        }

        if (length > maxBodyBytes)
        {
            return Admission.Refuse(413, ToolEnvelope.Failure(
                "payload_too_large",
                $"Request body is {Size(length)}; the bridge accepts at most {Size(maxBodyBytes)}. " +
                "The body was not read and nothing ran.",
                "Send less per call, or have a run_csharp snippet read large input from a file. A body " +
                "this size is often a serialisation accident: Windows PowerShell's ConvertTo-Json " +
                "expands a string read with Get-Content into its PSPath/PSDrive/PSProvider graph. Send " +
                "[string]$text, or read the file with [IO.File]::ReadAllText(path)."));
        }

        return Admission.Read((int)length);
    }

    private static string Size(long bytes) => string.Format(
        CultureInfo.InvariantCulture, "{0:N0} bytes ({1:N1} MB)", bytes, bytes / (1024.0 * 1024.0));

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

/// <summary>
/// The outcome of <see cref="HttpRequestGuard.Admit"/>. Either Refusal is set and goes back under
/// Status without the body being read, or it is null and ContentLength is how many body bytes to
/// read before dispatching.
/// </summary>
public sealed record Admission(int Status, ToolEnvelope? Refusal, int ContentLength)
{
    public bool Admitted => Refusal is null;

    internal static Admission Read(int contentLength) => new(200, null, contentLength);
    internal static Admission Refuse(int status, ToolEnvelope refusal) => new(status, refusal, 0);
}
