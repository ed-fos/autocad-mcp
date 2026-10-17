using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nodal.AcadMcp.Contracts;

namespace Nodal.AcadMcp.Addin;

/// <summary>
/// Minimal HTTP/1.1 server on loopback, hosted inside AutoCAD.
///
/// Deliberately TcpListener rather than HttpListener: HttpListener routes through http.sys,
/// which requires a `netsh http add urlacl` reservation (admin) for an arbitrary port.
/// A loopback socket needs no privilege, and AutoCAD normally runs unelevated.
///
/// SECURITY — read this before changing anything below the accept loop.
/// This endpoint dispatches run_csharp and run_lisp, which are unsandboxed arbitrary code
/// execution with the logged-in user's full privileges. Binding to loopback is NOT sufficient
/// protection on its own, for two reasons:
///   * Any web page the user visits can POST here cross-origin. A text/plain body is a
///     CORS-simple request, so no preflight fires and the call lands even though the reply
///     is unreadable — which is enough, because the payload is the attack.
///   * DNS rebinding makes an attacker domain resolve to 127.0.0.1, at which point the
///     request is same-origin and the REPLY is readable too: a full interactive shell.
/// Four checks close that off, in Authorize() below:
///   1. a per-session token, minted here and shared only through a per-user file
///   2. Host must be loopback and our port (this is what actually kills rebinding)
///   3. any Origin or Referer at all is rejected — a real MCP client never sends one
///   4. POST must declare application/json, which forces a preflight the browser will fail
/// Check 1 is the security boundary; 2-4 are defence in depth against a browser specifically.
/// </summary>
internal static class HttpBridge
{
    /// <summary>Request bodies above this are refused outright rather than allocated.</summary>
    private const int MaxBodyBytes = 16 * 1024 * 1024;

    private const int MaxHeaderBytes = 64 * 1024;

    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;

    internal static int Port { get; private set; }
    internal static bool IsRunning => _listener is not null;

    /// <summary>Per-session shared secret. Regenerated on every Start; never logged.</summary>
    private static string _token = string.Empty;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    internal static void Start(int port)
    {
        if (IsRunning)
            throw new AcadMcpException("already_running", $"Bridge is already listening on port {Port}.");

        // A second AutoCAD instance must not silently lose the bridge, so fall back to an
        // ephemeral port. The server finds us by instance file, not by assuming the default.
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (SocketException) when (port != 0)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }

        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _cts = new CancellationTokenSource();

        try
        {
            PublishInstanceFile();
        }
        catch
        {
            // Without the instance file nothing can discover or authenticate to us, so a bound
            // socket with IsRunning reporting healthy would be a lie. Unwind completely and let
            // the caller report the failure.
            Stop();
            throw;
        }

        // Capture the token NOW rather than reading _cts inside the lambda: the lambda runs on
        // the thread pool, and a fast START/STOP would have it dereference a null _cts.
        var ct = _cts.Token;
        _ = Task.Run(() => AcceptLoop(listener, ct));
    }

    internal static void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
        Port = 0;
        _token = string.Empty;

        try { File.Delete(AcadMcpProtocol.InstanceFile(Environment.ProcessId)); } catch { }
    }

    // ------------------------------------------------------------------ instance discovery

    /// <summary>
    /// Writes {pid, port, token} where the MCP server can find it. LOCALAPPDATA is already
    /// per-user on Windows, so the token is not readable by another account.
    /// </summary>
    private static void PublishInstanceFile()
    {
        var dir = AcadMcpProtocol.InstanceDirectory;
        Directory.CreateDirectory(dir);
        PruneDeadInstances(dir);

        var descriptor = new InstanceDescriptor
        {
            Pid = Environment.ProcessId,
            Port = Port,
            Token = _token,
            StartedUtc = DateTime.UtcNow.ToString("o"),
            Product = SafeProductName(),
        };

        File.WriteAllText(
            AcadMcpProtocol.InstanceFile(descriptor.Pid),
            JsonSerializer.Serialize(descriptor, Json),
            new UTF8Encoding(false));
    }

    /// <summary>An AutoCAD that crashed leaves its file behind; a stale token must not linger.</summary>
    private static void PruneDeadInstances(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid)) continue;
            if (pid == Environment.ProcessId) continue;

            var alive = true;
            try { using var _ = Process.GetProcessById(pid); }
            catch (ArgumentException) { alive = false; }
            catch { /* access denied still means it exists */ }

            if (!alive)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private static string? SafeProductName()
    {
        try { return Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("PRODUCT") as string; }
        catch { return null; }
    }

    // ------------------------------------------------------------------ accept / dispatch

    /// <summary>Consecutive accept failures tolerated before we conclude the listener is dead.</summary>
    private const int MaxConsecutiveAcceptFailures = 20;

    private static async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException)
            {
                // A per-connection error (RST during accept, transient resource pressure) must
                // not silently kill the listener while IsRunning still claims we are up. But a
                // PERMANENTLY broken listener must not spin the CPU inside the user's CAD app
                // either, so back off and eventually give up honestly.
                if (ct.IsCancellationRequested) return;

                if (++failures >= MaxConsecutiveAcceptFailures)
                {
                    Stop();
                    return;
                }

                try { await Task.Delay(50 * failures, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            // Deliberately NOT passing ct to Task.Run: if it is already cancelled the delegate
            // never runs, so the accepted socket would never be disposed. HandleClient owns it.
            _ = Task.Run(() => HandleClient(client, ct));
        }
    }

    private static async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();

                var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                if (request is null) return;

                var req = request.Value;

                // Note the ordering: ReadRequestAsync has already drained the body, deliberately.
                // Closing a socket with unread bytes still pending makes Windows send RST, which
                // discards the refusal we are about to write. MaxBodyBytes bounds what an
                // unauthenticated caller can make us allocate.
                var denial = HttpRequestGuard.Authorize(req.Method, req.Headers, _token, Port);
                if (denial is not null)
                {
                    // Deliberately terse and identical for every cause: a rejected caller learns
                    // nothing about which check it failed, or even what this service is.
                    var refusal = JsonSerializer.SerializeToUtf8Bytes(
                        ToolEnvelope.Failure("forbidden", "Request rejected."), Json);
                    await WriteResponseAsync(stream, denial.Value, refusal, ct).ConfigureAwait(false);
                    return;
                }

                var envelope = await DispatchAsync(req.Method, req.Path, req.Body).ConfigureAwait(false);

                var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
                await WriteResponseAsync(stream, 200, payload, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A dead client socket must never take AutoCAD down with it.
            }
        }
    }

    private static async Task<ToolEnvelope> DispatchAsync(string method, string path, string body)
    {
        var route = path.Trim('/');

        if (route.Equals(AcadMcpProtocol.PingRoute, StringComparison.OrdinalIgnoreCase))
            return ToolEnvelope.Success(new
            {
                alive = true,
                port = Port,
                pid = Environment.ProcessId,
                document = SafeDocumentName(),
            });

        if (route.Equals(AcadMcpProtocol.ListRoute, StringComparison.OrdinalIgnoreCase))
            return ToolEnvelope.Success(Router.List());

        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            return ToolEnvelope.Failure("bad_method", $"Tool calls must use POST; got {method}.");

        // A body that parses but is not an object (an array, a bare string) would otherwise reach
        // every tool as "no arguments" and run on defaults.
        var bodyError = ToolArgs.TryParseBody(body, out var args);
        if (bodyError is not null)
            return ToolEnvelope.Failure(bodyError.Code, bodyError.Message, bodyError.Detail);

        return await Router.Invoke(route, args).ConfigureAwait(false);
    }

    /// <summary>Lets a client notice it is talking to the instance it thinks it is.</summary>
    private static string? SafeDocumentName()
    {
        try { return Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?.Name; }
        catch { return null; }
    }

    // ------------------------------------------------------------------ wire

    private readonly record struct Request(
        string Method, string Path, string Body, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new MemoryStream();
        var one = new byte[1];
        var matched = 0;

        // Read until CRLFCRLF terminates the header block.
        while (matched < 4)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0) return null;

            header.WriteByte(one[0]);
            matched = one[0] switch
            {
                (byte)'\r' when matched is 0 or 2 => matched + 1,
                (byte)'\n' when matched is 1 or 3 => matched + 1,
                _ => 0,
            };

            if (header.Length > MaxHeaderBytes) return null; // header flood guard
        }

        var headerText = Encoding.UTF8.GetString(header.ToArray());

        // Parsing lives in Contracts so the admission rules can be unit-tested without AutoCAD.
        if (!HttpRequestGuard.TryParseHead(headerText, out var method, out var path, out var headers))
            return null;

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var cl))
            _ = int.TryParse(cl, out contentLength);

        if (contentLength < 0 || contentLength > MaxBodyBytes) return null;

        var body = string.Empty;
        if (contentLength > 0)
        {
            var buffer = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, contentLength - offset), ct)
                                       .ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
            }
            body = Encoding.UTF8.GetString(buffer, 0, offset);
        }

        return new Request(method, path, body, headers);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, byte[] payload, CancellationToken ct)
    {
        var reason = status switch
        {
            200 => "OK",
            403 => "Forbidden",
            413 => "Payload Too Large",
            415 => "Unsupported Media Type",
            _ => "Bad Request",
        };

        var head = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            // No Access-Control-Allow-* of any kind: a preflight must fail.
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
