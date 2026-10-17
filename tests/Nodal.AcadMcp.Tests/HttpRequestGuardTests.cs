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

    // ------------------------------------------------------------------ admission and body size

    private const int MaxBody = 16 * 1024 * 1024;

    private static Admission Admit(string method, Dictionary<string, string> headers) =>
        HttpRequestGuard.Admit(method, headers, Token, Port, MaxBody);

    private static readonly ToolEnvelope Terse = ToolEnvelope.Failure("forbidden", "Request rejected.");

    [Fact]
    public void Admits_an_authorized_body_within_the_limit_and_says_how_much_to_read()
    {
        var admission = Admit("POST", Valid(("Content-Length", "1234")));

        Assert.True(admission.Admitted);
        Assert.Equal(1234, admission.ContentLength);
    }

    [Fact]
    public void Admits_a_body_of_exactly_the_limit()
    {
        var admission = Admit("POST", Valid(("Content-Length", MaxBody.ToString())));

        Assert.True(admission.Admitted);
        Assert.Equal(MaxBody, admission.ContentLength);
    }

    [Fact]
    public void Treats_a_missing_content_length_as_an_empty_body()
    {
        var admission = Admit("GET", Valid(("Content-Type", null!)));

        Assert.True(admission.Admitted);
        Assert.Equal(0, admission.ContentLength);
    }

    [Fact]
    public void Refuses_an_oversized_authorized_body_with_413_saying_the_limit_and_the_size()
    {
        var admission = Admit("POST", Valid(("Content-Length", "20971520")));

        Assert.False(admission.Admitted);
        Assert.Equal(413, admission.Status);
        Assert.Equal("payload_too_large", admission.Refusal!.Error!.Code);
        Assert.Contains("20,971,520 bytes", admission.Refusal.Error.Message);
        Assert.Contains("16,777,216 bytes", admission.Refusal.Error.Message);
    }

    [Fact]
    public void Reports_the_real_size_of_a_content_length_past_int_range()
    {
        // int.TryParse used to fail on this and read it as zero.
        var admission = Admit("POST", Valid(("Content-Length", "20000000000")));

        Assert.Equal(413, admission.Status);
        Assert.Contains("20,000,000,000 bytes", admission.Refusal!.Error!.Message);
    }

    [Theory]
    [InlineData((string?)null)]
    [InlineData("evil.example")]
    public void An_unauthorized_oversized_body_gets_the_same_terse_403_as_any_other(string? host)
    {
        // Missing token, or a rebound Host: either way the caller must not learn that a body
        // limit exists, so the refusal is byte-for-byte what a small request gets.
        var headers = host is null
            ? Valid((AcadMcpProtocol.TokenHeader, null!), ("Content-Length", "20971520"))
            : Valid(("Host", host), ("Content-Length", "20971520"));
        var small = host is null
            ? Valid((AcadMcpProtocol.TokenHeader, null!), ("Content-Length", "10"))
            : Valid(("Host", host), ("Content-Length", "10"));

        var big = Admit("POST", headers);

        Assert.Equal(403, big.Status);
        Assert.Equal(Terse, big.Refusal);
        Assert.Equal(Admit("POST", small), big);
    }

    [Fact]
    public void An_unauthorized_malformed_content_length_gets_the_terse_403_too()
    {
        var headers = Valid((AcadMcpProtocol.TokenHeader, "wrong"), ("Content-Length", "-1"));

        var admission = Admit("POST", headers);

        Assert.Equal(403, admission.Status);
        Assert.Equal(Terse, admission.Refusal);
    }

    [Fact]
    public void A_wrong_content_type_is_refused_as_415_before_the_size_is_considered()
    {
        var headers = Valid(("Content-Type", "text/plain"), ("Content-Length", "20971520"));

        var admission = Admit("POST", headers);

        Assert.Equal(415, admission.Status);
        Assert.Equal(Terse, admission.Refusal);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("12abc")]
    [InlineData("1.5")]
    [InlineData("")]
    public void Refuses_an_authorized_malformed_content_length_with_400(string value)
    {
        var admission = Admit("POST", Valid(("Content-Length", value)));

        Assert.Equal(400, admission.Status);
        Assert.Equal("bad_request", admission.Refusal!.Error!.Code);
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
