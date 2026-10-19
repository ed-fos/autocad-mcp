using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Nodal.AcadMcp.Contracts;

// ---------------------------------------------------------------------------------------------
// Nodal.AcadMcp MCP server.
//
// A deliberately thin proxy: it owns no tool definitions of its own. At list-time it asks the
// in-process add-in what tools exist and forwards the answer; at call-time it posts the arguments
// through. Adding a tool to the add-in therefore needs no change here and no redeploy.
//
// Discovery is deliberately LAZY. An MCP client normally launches this process when the client
// starts, which is usually before AutoCAD is even open — so the add-in is located on every
// request, not once at startup. Start AutoCAD later and the tools simply appear.
// ---------------------------------------------------------------------------------------------

var portOverride = int.TryParse(Environment.GetEnvironmentVariable("NODAL_ACAD_PORT"), out var p)
    ? p
    : (int?)null;

var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

var PermissiveSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","additionalProperties":true}""");

var builder = Host.CreateApplicationBuilder(args);

// stdout IS the MCP transport. Anything written there that is not a framed message corrupts
// the protocol, so every log line goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "nodal-acad", Version = ThisVersion() };
    })
    .WithStdioServerTransport()
    .WithListToolsHandler(async (_, ct) =>
    {
        var tools = new List<Tool>();

        var instance = FindInstance(portOverride);
        if (instance is null)
            return new ListToolsResult { Tools = [UnavailableTool(NoInstanceMessage(portOverride))] };

        try
        {
            using var request = Build(HttpMethod.Get, instance, AcadMcpProtocol.ListRoute);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new ListToolsResult { Tools = [UnavailableTool(RejectedMessage(response.StatusCode))] };

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in result.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var description = item.TryGetProperty("description", out var d)
                        ? d.GetString() ?? string.Empty
                        : string.Empty;

                    var mutating = item.TryGetProperty("mutating", out var m) &&
                                   m.ValueKind == JsonValueKind.True;

                    var undo = item.TryGetProperty("undo", out var u) ? u.GetString() : null;

                    tools.Add(new Tool
                    {
                        Name = name!,
                        Description = UndoPrefix(mutating, undo) + description,
                        // Older add-ins do not advertise schemas. New tools can expose their
                        // actual contract without changing discovery for existing tools.
                        InputSchema = item.TryGetProperty("inputSchema", out var schema)
                            && schema.ValueKind == JsonValueKind.Object ? schema.Clone() : PermissiveSchema,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // Surfacing a single diagnostic tool beats an empty list with no explanation.
            tools.Add(UnavailableTool(
                $"Cannot reach AutoCAD on {AcadMcpProtocol.BaseUrl(instance.Port)} ({ex.Message}). " +
                "Check that the drawing session is still open and NODALMCPSTATUS reports it listening."));
        }

        return new ListToolsResult { Tools = tools };
    })
    .WithCallToolHandler(async (ctx, ct) =>
    {
        var name = ctx.Params?.Name;
        if (string.IsNullOrWhiteSpace(name))
            return Fail("No tool name supplied.");

        var instance = FindInstance(portOverride);
        if (instance is null)
            return Fail(NoInstanceMessage(portOverride));

        var payload = ctx.Params?.Arguments is { Count: > 0 } a
            ? JsonSerializer.Serialize(a)
            : "{}";

        try
        {
            // A client may cache this diagnostic tool while AutoCAD is offline. Keep that
            // cached entry useful after reconnecting; never forward it as an unknown tool.
            if (name == "acad_bridge_unavailable")
            {
                using var discovery = Build(HttpMethod.Get, instance, AcadMcpProtocol.ListRoute);
                using var discovered = await http.SendAsync(discovery, ct).ConfigureAwait(false);
                var catalog = await discovered.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!discovered.IsSuccessStatusCode) return Fail(RejectedMessage(discovered.StatusCode));
                return new CallToolResult { Content = [new TextContentBlock { Text = "AutoCAD is connected. Refresh the MCP tool list to discover its tools. Current catalog: " + catalog }] };
            }
            using var request = Build(HttpMethod.Post, instance, name);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Fail(RejectedMessage(response.StatusCode));

            // The bridge always answers 200 and signals tool failure inside the envelope.
            var isError = false;
            try
            {
                using var doc = JsonDocument.Parse(body);
                isError = doc.RootElement.TryGetProperty("ok", out var ok) &&
                          ok.ValueKind == JsonValueKind.False;
            }
            catch (JsonException) { /* pass the raw body through */ }

            // A tool that returns a picture (plot_preview) gets it shown to the model as an image,
            // and the base64 is dropped from the text so it is not paid for twice.
            if (!isError && ToolResultImage.TrySplit(body, out var text, out var image, out var mimeType))
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = text }, ImageContentBlock.FromBytes(image, mimeType)],
                    IsError = false,
                };

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = body }],
                IsError = isError,
            };
        }
        catch (Exception ex)
        {
            return Fail($"Could not reach AutoCAD on {AcadMcpProtocol.BaseUrl(instance.Port)}: {ex.Message}");
        }
    });

await builder.Build().RunAsync().ConfigureAwait(false);
return;

// ------------------------------------------------------------------------------- helpers

/// <summary>
/// Tells the model the truth about reversibility. The add-in classifies each tool; anything
/// that runs caller-supplied code or writes profile state must NOT be advertised as one
/// Ctrl+Z, because a model that believes that will try it speculatively on a live drawing.
/// </summary>
static string UndoPrefix(bool mutating, string? undo)
{
    if (!mutating) return string.Empty;

    return undo switch
    {
        UndoKind.ToolDefined =>
            "[MODIFIES THE DRAWING - NOT a single undo step. Reversibility is whatever the code or "
          + "command you send happens to produce, and anything it does outside the drawing (files, "
          + "processes, settings) cannot be undone at all] ",
        UndoKind.NotUndoable =>
            "[CHANGES PERSISTENT STATE - Ctrl+Z CANNOT undo this] ",
        _ => "[MODIFIES THE DRAWING - one Ctrl+Z undoes it] ",
    };
}

/// <summary>
/// Locates a live add-in by reading the per-user instance directory. Prefers an explicit
/// NODAL_ACAD_PORT, otherwise takes the most recently started live session.
/// </summary>
static InstanceDescriptor? FindInstance(int? wantedPort)
{
    var dir = AcadMcpProtocol.InstanceDirectory;
    if (!Directory.Exists(dir)) return null;

    var candidates = new List<InstanceDescriptor>();

    foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
    {
        InstanceDescriptor? descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<InstanceDescriptor>(
                File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { continue; }

        if (descriptor is null || descriptor.Port <= 0 || string.IsNullOrEmpty(descriptor.Token)) continue;
        if (wantedPort is not null && descriptor.Port != wantedPort) continue;

        // A crashed AutoCAD leaves its file behind; never hand back a dead session.
        try { using var _ = Process.GetProcessById(descriptor.Pid); }
        catch (ArgumentException) { continue; }
        catch { /* access denied still means it is alive */ }

        candidates.Add(descriptor);
    }

    return candidates
        .OrderByDescending(c => c.StartedUtc, StringComparer.Ordinal)
        .FirstOrDefault();
}

static HttpRequestMessage Build(HttpMethod method, InstanceDescriptor instance, string route)
{
    var request = new HttpRequestMessage(method, AcadMcpProtocol.BaseUrl(instance.Port) + route);
    request.Headers.Add(AcadMcpProtocol.TokenHeader, instance.Token);
    return request;
}

static string NoInstanceMessage(int? wantedPort)
{
    var scope = wantedPort is null
        ? "No AutoCAD session is currently sharing a Nodal AcadMcp bridge."
        : $"No AutoCAD session is sharing a Nodal AcadMcp bridge on port {wantedPort}.";

    return scope + " Start AutoCAD, then type NODALMCPSTART at the command line. "
         + "The bridge is opt-in per session because it can execute arbitrary code, so it does "
         + "not open by itself. NODALMCPSTATUS reports whether it is listening.";
}

static string RejectedMessage(System.Net.HttpStatusCode status) =>
    status == System.Net.HttpStatusCode.Forbidden
        ? "AutoCAD refused the request. The session token is stale — this usually means the bridge "
        + "was stopped and restarted, or AutoCAD was reopened. Retry; if it persists, run "
        + "NODALMCPSTOP then NODALMCPSTART in AutoCAD."
        : $"AutoCAD refused the request ({(int)status} {status}).";

static Tool UnavailableTool(string message) => new()
{
    Name = "acad_bridge_unavailable",
    Description = message,
    InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","additionalProperties":true}"""),
};

static string ThisVersion() =>
    Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0]
    ?? "0.0.0";

static CallToolResult Fail(string message) => new()
{
    Content = [new TextContentBlock { Text = message }],
    IsError = true,
};
