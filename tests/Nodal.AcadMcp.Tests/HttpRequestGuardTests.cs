using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

/// <summary>
/// The bridge dispatches run_csharp and run_lisp, which are unsandboxed code execution. These
/// tests are the regression net for the checks that stand in front of that — in particular the
/// browser-reachability class of attack (CSRF and DNS rebinding against a loopback port), which
/// is what makes an otherwise-private developer tool dangerous to publish.
/// </summary>
public class HttpRequestGuardTests
{
    private const int Port = 48885;
    private const string Token = "0123456789ABCDEF0123456789ABCDEF";

    private static Dictionary<string, string> Valid(params (string Key, string Value)[] overrides)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AcadMcpProtocol.TokenHeader] = Token,
            ["Host"] = $"127.0.0.1:{Port}",
            ["Content-Type"] = "application/json",
        };

        foreach (var (key, value) in overrides)
        {
            if (value is null) headers.Remove(key);
            else headers[key] = value;
        }

        return headers;
    }

    // ------------------------------------------------------------------ the happy path

    [Fact]
    public void Allows_a_well_formed_authenticated_post()
    {
        Assert.Null(HttpRequestGuard.Authorize("POST", Valid(), Token, Port));
    }

    [Fact]
    public void Allows_charset_on_the_content_type()
    {
        var headers = Valid(("Content-Type", "application/json; charset=utf-8"));
        Assert.Null(HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    [Fact]
    public void Allows_localhost_spelling_of_the_host_header()
    {
        var headers = Valid(("Host", $"localhost:{Port}"));
        Assert.Null(HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    [Fact]
    public void Allows_get_without_a_content_type()
    {
        var headers = Valid(("Content-Type", null!));
        Assert.Null(HttpRequestGuard.Authorize("GET", headers, Token, Port));
    }

    // ------------------------------------------------------------------ token

    [Fact]
    public void Rejects_a_missing_token()
    {
        var headers = Valid((AcadMcpProtocol.TokenHeader, null!));
        Assert.Equal(403, HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    [Fact]
    public void Rejects_a_wrong_token()
    {
        var headers = Valid((AcadMcpProtocol.TokenHeader, new string('F', Token.Length)));
        Assert.Equal(403, HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    [Fact]
    public void Rejects_a_token_that_is_only_a_correct_prefix()
    {
        var headers = Valid((AcadMcpProtocol.TokenHeader, Token[..^1]));
        Assert.Equal(403, HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    [Fact]
    public void Rejects_everything_when_the_bridge_has_no_token_yet()
    {
        // Stop() clears the token; a request racing that must not be admitted.
        Assert.Equal(403, HttpRequestGuard.Authorize("POST", Valid(), string.Empty, Port));
    }

    // ------------------------------------------------------------------ DNS rebinding

    [Fact]
    public void Rejects_a_rebound_attacker_host_even_with_a_valid_token()
    {
        // The rebinding case: the browser resolved evil.example to 127.0.0.1, so the socket
        // arrives on loopback, but the Host header still carries the attacker's name.
        var headers = Valid(("Host", "evil.example"));
        Assert.Equal(403, HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    [Fact]
    public void Rejects_a_host_on_the_wrong_port()
    {
        var headers = Valid(("Host", "127.0.0.1:9999"));
        Assert.Equal(403, HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    [Fact]
    public void Rejects_a_missing_host()
    {
        var headers = Valid(("Host", null!));
        Assert.Equal(403, HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    // ------------------------------------------------------------------ browser CSRF

    [Fact]
    public void Rejects_anything_carrying_an_origin()
    {
        var headers = Valid(("Origin", "https://evil.example"));
        Assert.Equal(403, HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    [Fact]
    public void Rejects_anything_carrying_a_referer()
    {
        var headers = Valid(("Referer", "https://evil.example/page"));
        Assert.Equal(403, HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    [Fact]
    public void Rejects_a_null_origin_from_a_sandboxed_frame()
    {
        var headers = Valid(("Origin", "null"));
        Assert.Equal(403, HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data")]
    public void Rejects_the_cors_simple_content_types_that_avoid_a_preflight(string contentType)
    {
        // These three are exactly the set a browser can POST cross-origin without a preflight.
        var headers = Valid(("Content-Type", contentType));
        Assert.Equal(415, HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    [Fact]
    public void Rejects_a_post_with_no_content_type_at_all()
    {
        var headers = Valid(("Content-Type", null!));
        Assert.Equal(415, HttpRequestGuard.Authorize("POST", headers, Token, Port));
    }

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void Parses_method_path_and_headers()
    {
        var head = "POST /run_csharp HTTP/1.1\r\nHost: 127.0.0.1:48885\r\nContent-Length: 12\r\n\r\n";

        Assert.True(HttpRequestGuard.TryParseHead(head, out var method, out var path, out var headers));
        Assert.Equal("POST", method);
        Assert.Equal("/run_csharp", path);
        Assert.Equal("127.0.0.1:48885", headers["Host"]);
        Assert.Equal("12", headers["Content-Length"]);
    }

    [Fact]
    public void Header_lookup_is_case_insensitive()
    {
        var head = $"POST / HTTP/1.1\r\nhost: 127.0.0.1:{Port}\r\nORIGIN: https://evil.example\r\n\r\n";

        Assert.True(HttpRequestGuard.TryParseHead(head, out _, out _, out var headers));
        Assert.True(headers.ContainsKey("Host"));

        // Case must not be a way to smuggle an Origin past the check.
        Assert.True(headers.ContainsKey("Origin"));
    }

    [Fact]
    public void A_duplicated_host_header_cannot_hide_the_attacker_value()
    {
        // Request smuggling shape: a second Host appended after a benign one. Last value wins,
        // so the attacker's value is what gets checked — and rejected.
        var head = $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{Port}\r\nHost: evil.example\r\n\r\n";

        Assert.True(HttpRequestGuard.TryParseHead(head, out var method, out _, out var headers));
        headers[AcadMcpProtocol.TokenHeader] = Token;
        headers["Content-Type"] = "application/json";

        Assert.Equal(403, HttpRequestGuard.Authorize(method, headers, Token, Port));
    }

    [Fact]
    public void Rejects_a_garbage_request_line()
    {
        Assert.False(HttpRequestGuard.TryParseHead("nonsense\r\n\r\n", out _, out _, out _));
    }

    [Fact]
    public void Rejects_an_empty_head()
    {
        Assert.False(HttpRequestGuard.TryParseHead(string.Empty, out _, out _, out _));
    }

    [Fact]
    public void FixedTimeEquals_distinguishes_equal_length_strings()
    {
        Assert.True(HttpRequestGuard.FixedTimeEquals("abcd", "abcd"));
        Assert.False(HttpRequestGuard.FixedTimeEquals("abcd", "abce"));
        Assert.False(HttpRequestGuard.FixedTimeEquals("abcd", "abcde"));
    }
}
