# Nodal.AcadMcp

An MCP server that lets a language model drive **AutoCAD 2027** and **AutoCAD Architecture** —
159 tools covering entities, blocks, annotation, drafting symbols and checks, layouts, sheets and PDF plotting, 3D solids, and
the full ACA object model: walls, doors, windows, spaces, slabs, roofs, mass elements, column
grids, structural members, curtain walls, stairs, railings, and generated sections and elevations.

> **Status: alpha.** This is one person's tool, shared in case it is useful to someone else. I
> use it daily on my own drawings; it has rough edges and there is no support promise.

> **⚠️ Read [SECURITY.md](SECURITY.md) before installing.** This puts an endpoint inside your
> running AutoCAD and three of its tools execute arbitrary code. That is deliberate and it is
> defensible, but it is not something to install casually.

> **No warranty. Use at your own risk.** It can modify and destroy drawings, some of it is not
> undoable, and a language model is deciding what it does. See [Disclaimer](#disclaimer).

## For AI agents: connecting

If you are a model reading this to reach AutoCAD:

1. **Use the MCP tools if you have them** (`acad_status`, `run_csharp` and so on). If the only
   one is `acad_bridge_unavailable`, the bridge is closed: ask the user to click **Start** on the
   **Nodal MCP** ribbon panel or type `NODALMCPSTART`, then refresh the tool list.
2. **No AutoCAD tools at all?** Use the server exe as a command-line client:
   `Nodal.AcadMcp.Server.exe call <tool> @args.json`. It finds AutoCAD and sends the session token
   for you; `tools` lists every tool and `help` gives the usage. It is installed at
   `%LOCALAPPDATA%\Nodal.AcadMcp\server\`. The protocol underneath is under
   [Raw HTTP fallback](#raw-http-fallback).
3. **Never drive AutoCAD over COM** (`GetActiveObject("AutoCAD.Application")`). Those calls come
   from outside AutoCAD with no main-thread marshalling and no undo grouping, and one bad
   `AddRegion` call crashed AutoCAD in the middle of a session.
4. **Read the working rules before drawing:** `Nodal.AcadMcp.Server.exe guide`, or `GET /_guide`
   on the bridge. An MCP client already receives them as the server's instructions.

The URL that `NODALMCPSTATUS` prints answers `403` on its own: every request needs the token.

---

## Why not the Autodesk one?

AutoCAD 2027 ships its own first-party MCP server, and if it covers what you need, **use
that** — it is supported, signed, and installed already.

This exists because it covers ground the first-party server does not:

| | Autodesk's MCP | Nodal.AcadMcp |
|---|---|---|
| Tools | 9 | 159 |
| AutoCAD Architecture | none | 79 tools across walls, doors, windows, spaces, slabs, roofs, masses, grids, members, assemblies, sections |
| 3D solids | no | 15 tools (primitives, booleans, extrude/revolve/loft/sweep, slice) |
| Escape hatches | no | `run_csharp`, `run_lisp`, `send_command` |
| Supported | yes, by Autodesk | no |
| Signed | yes | no |

If you work in ACA, that second row is the entire reason this repository exists.

---

## Requirements

- **AutoCAD 2027** (R26.0). Not 2026 or earlier — see [Supported versions](#supported-versions).
- **AutoCAD Architecture 2027** for the 79 ACA tools. Everything else works on vanilla AutoCAD;
  the ACA tools stay visible and return `aca_unavailable` rather than disappearing.
- Windows x64.
- **Installing from a release needs nothing else** — the server in the zip is self-contained.
- **Building from source** needs the **.NET 10 SDK**. Note that a `dotnet` on your `PATH` is
  often the runtime-only install with no SDK at all; `deploy.ps1` probes for a real 10.x SDK
  and falls back to a user-local one rather than failing halfway through.

---

## Install

### From a release

1. Download the release zip and unblock it (right-click → Properties → Unblock).
2. Copy the `Nodal.AcadMcp.bundle` folder to
   `%APPDATA%\Autodesk\ApplicationPlugins\`.
3. Copy the `server` folder somewhere stable, e.g.
   `%LOCALAPPDATA%\Nodal.AcadMcp\server`.
4. Restart AutoCAD.

### From source

```bash
git clone https://github.com/ed-fos/autocad-mcp.git
cd autocad-mcp
powershell -ExecutionPolicy Bypass -File deploy.ps1
```

That builds both halves, installs the bundle to `%APPDATA%\Autodesk\ApplicationPlugins`, and
publishes the server to `%LOCALAPPDATA%\Nodal.AcadMcp\server`. Add `-Package` to also produce
a distributable zip in `artifacts/`.

The script picks the first `dotnet` that actually has a .NET 10 SDK — checking `PATH` first,
then `~\.dotnet` and Program Files — rather than the first one it can find. Override with
`-DotnetPath`.

---

## Wire it up

### 1. Open the bridge in AutoCAD

Click **Start** on the **Nodal MCP** panel of the **Add-ins** ribbon tab, or type:

```
NODALMCPSTART
```

**This does not happen automatically, on purpose.** The add-in loads with AutoCAD so the
commands and the ribbon exist, but it opens no socket — the bridge executes arbitrary code, so
starting it is a deliberate act each session. **Status** reports whether it is listening and on
which port; **Stop** closes it and invalidates the session token.

If you genuinely want the bridge up on every start, put `NODALMCPSTART` in your `acaddoc.lsp`.

### 2. Point your MCP client at the server

Claude Desktop (`claude_desktop_config.json`) or Claude Code (`.mcp.json`):

```json
{
  "mcpServers": {
    "autocad": {
      "command": "C:\\Users\\YOU\\AppData\\Local\\Nodal.AcadMcp\\server\\Nodal.AcadMcp.Server.exe"
    }
  }
}
```

In Claude Code, prefer registering it for every folder over one project's `.mcp.json`, which
only works in that folder and only after you approve it there:

```bash
claude mcp add --scope user nodal-acad -- "C:\Users\YOU\AppData\Local\Nodal.AcadMcp\server\Nodal.AcadMcp.Server.exe"
```

No arguments and no environment needed. The server finds the running AutoCAD by reading a
per-user instance file, so it does not care which port was used or in which order you started
things — you can launch the MCP client before AutoCAD.

If the bridge is not up, the server reports a single `acad_bridge_unavailable` tool whose
description tells you what to do, rather than failing silently with an empty list. Once you
run `NODALMCPSTART`, the real tools appear on the client's next tool listing — most clients
cache that, so you may need to reconnect the server or restart the client to see them.

### Troubleshooting

| Symptom | Cause |
|---|---|
| `Unknown command "NODALMCPSTART"` | The bundle did not load. Check it is at `%APPDATA%\Autodesk\ApplicationPlugins\Nodal.AcadMcp.bundle`, that the zip was unblocked *before* extracting, and that `PackageContents.xml` says `LoadOnAutoCADStartup="true"`. As a fallback, `NETLOAD` the add-in DLL directly. |
| No **Nodal MCP** ribbon panel | The ribbon is cosmetic and fails silently by design. Check the **Add-ins** tab is enabled; if the Add-ins tab is absent the panel is placed on a **Nodal** tab instead. The commands work either way. |
| Only `acad_bridge_unavailable` is listed | The bridge is not open in any AutoCAD. Run `NODALMCPSTATUS`. |
| Tools still missing after `NODALMCPSTART` | Your client cached the tool list. Reconnect the server. |
| Server is in `.mcp.json` but no AutoCAD tools appear in Claude Code | Claude Code starts a server from a project's `.mcp.json` only once you approve it for that folder, and a session opened in any other folder never sees it. It is the most common reason an agent ends up without the tools. Register it for every folder instead, `claude mcp add --scope user nodal-acad -- "C:\Users\YOU\AppData\Local\Nodal.AcadMcp\server\Nodal.AcadMcp.Server.exe"`, remove the project entry so the two do not shadow each other, and start a new session. Until then, use `Nodal.AcadMcp.Server.exe call` or the [raw HTTP fallback](#raw-http-fallback). |
| `403` / "session token is stale" | The bridge restarted. Retry; the server re-reads the token per request. |
| ACA tools return `aca_unavailable` | Either ACA is not loaded in this session, or the add-in was built on a machine without ACA installed. |

### Raw HTTP fallback

Every tool can be called without an MCP client. The easiest way is the server exe itself, which
does the discovery and the token for you and prints the tool's JSON reply:

```powershell
$server = "$env:LOCALAPPDATA\Nodal.AcadMcp\server\Nodal.AcadMcp.Server.exe"
& $server ping
& $server tools                      # one line per tool; 'tools create_mleader' for its schema
& $server call acad_status
& $server call run_csharp "@$PWD\stage1.json" --timeout 600
```

From Windows PowerShell put the arguments in a file and pass `@file`: PowerShell strips the
quotes out of inline JSON. The exit code is 0 when the tool worked, 1 when it reported an error
and 2 when AutoCAD could not be reached.

Underneath is plain HTTP against the bridge. This is the same interface the server uses, and it
is how whole drawing sessions were driven when the MCP tools failed to load.

- `POST /{tool}` with the tool's arguments as a JSON object calls it. `GET /_tools` lists every
  tool with its description, `GET /_ping` checks the bridge is alive, and `GET /_guide` returns
  the working rules an MCP client gets as server instructions.
- Read the port and token from the instance file the add-in writes,
  `%LOCALAPPDATA%\Nodal.AcadMcp\instances\<pid>.json`. Take the **newest file whose process is
  still running**: a crashed AutoCAD leaves its file behind.
- Send the token as `X-Nodal-Token`. `Host` must be `127.0.0.1:<port>` or `localhost:<port>`, and
  a request with an `Origin` or `Referer` header is refused. A POST needs
  `Content-Type: application/json`. Anything wrong here gets a bare `403` or `415`.
- Send the body as UTF-8 **bytes** and decode the reply as UTF-8. Once a request is admitted, the
  reply is HTTP 200 whether the tool worked or not: `{"ok": true, "result": ...}` or
  `{"ok": false, "error": {"code", "message", "detail"}}`.
- **Never pass a `Get-Content` result to `ConvertTo-Json`.** In Windows PowerShell it serializes
  the string's hidden `PSPath`/`PSProvider` properties, producing `{"value": ..., "PSPath": ...}`.
  The bridge rejects that as `bad_argument`, and at a large `-Depth` it can run for minutes before
  anything is sent. Read files with `[IO.File]::ReadAllText`.
- In Windows PowerShell, set `[Net.ServicePointManager]::Expect100Continue = $false`. It saves
  about 350 ms per call; it is not needed for correctness.

```powershell
[Net.ServicePointManager]::Expect100Continue = $false
function Invoke-Acad([string]$Tool, $Arguments = @{}) {
    $file = Get-ChildItem "$env:LOCALAPPDATA\Nodal.AcadMcp\instances\*.json" |
        Where-Object { Get-Process -Id $_.BaseName -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $instance = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
    $body = [Text.Encoding]::UTF8.GetBytes(($Arguments | ConvertTo-Json -Depth 10 -Compress))
    $reply = Invoke-WebRequest "http://127.0.0.1:$($instance.port)/$Tool" -Method Post `
        -Headers @{ 'X-Nodal-Token' = $instance.token } -ContentType 'application/json; charset=utf-8' `
        -Body $body -UseBasicParsing
    [Text.Encoding]::UTF8.GetString($reply.RawContentStream.ToArray()) | ConvertFrom-Json
}

Invoke-Acad run_csharp @{ code = [IO.File]::ReadAllText("$PWD\stage1.cs") }
```

A tool call returns when the tool does. AutoCAD runs on its main thread, so a snippet that hangs
holds the call open; give the client a timeout you can live with.

---

## How it works

```
MCP client  ──stdio──▶  Nodal.AcadMcp.Server.exe  ──HTTP/loopback──▶  add-in inside acad.exe
                        (thin proxy, owns no                          (owns all 159 tools,
                         tool definitions)                             marshals to main thread)
```

The server is deliberately thin: at list-time it asks the add-in what tools exist and forwards
the answer. **Adding a tool to the add-in needs no server change and no redeploy of the
server.** The server adds two things. A result carrying an `image` (as `plot_preview`'s and
`export_drawing_preview`'s do) reaches the model as MCP image content, so it can look at the
plot. And it sends the working rules (`Nodal.AcadMcp.Server.exe guide` prints them) as the MCP
server's instructions, which a client puts in front of the model for the whole session.

Run with a verb, the same exe is a command-line client for agents without MCP tools; see
[Raw HTTP fallback](#raw-http-fallback).

Every request carries a per-session token; the add-in also checks `Host` and rejects anything
carrying an `Origin`. See [SECURITY.md](SECURITY.md) for why all of that is necessary for a
loopback port.

The add-in marshals every call onto AutoCAD's main thread via `Application.Idle` — touching
the AutoCAD API from a worker thread is an immediate hard crash, so this is not optional.

### Undo honesty

Each tool declares what `Ctrl+Z` actually takes back, and the server turns that into the first
thing the model reads:

- `single-step`: the whole call commits as one undo step (almost every typed tool)
- `tool-defined`: `run_csharp`, `run_lisp`, `send_command`: reversibility is whatever your
  code produced, and side effects outside the drawing are not undoable at all. Also tools that
  commit in more than one step: `create_viewport` (see below), `create_roof` for any outline
  that is not four-sided (it goes through ROOFCONVERT), `create_center_mark` and
  `create_centerline` (the command, then a layer change), and `read_pdf_page`, which imports a
  page and then undoes back to its own mark, leaving the drawing as it was
- `not-undoable`: `set_system_variable` writes profile state that is not on the undo stack;
  the checkpoint, preview and plot tools write files

`create_viewport` commits in up to four steps because AutoCAD 2027 has two paper-space failures
that lose unsaved work: making a layout current in the same commit that erased viewports already
on it hangs AutoCAD, and turning on a viewport created in the same commit crashes it
intermittently. The tool switches layouts, creates the viewports switched off, and turns them on,
each in its own commit, and never erases anything.

This matters more than it sounds: a model that believes a call is one keystroke from being
undone will try it speculatively on a live drawing.

---

## Tool catalog

**Plain AutoCAD: 80 tools**

| Group | Tools |
|---|---|
| Session (3) | `acad_status` `list_documents` `list_layers` |
| Workflow verification (3) | `checkpoint_drawing` `export_drawing_preview` `validate_model` |
| Drafting (6) | `create_projection_symbol` `create_slope_symbol` `create_center_mark` `create_centerline` `check_drafting` `read_pdf_page` |
| Entities (15) | `create_line` `create_polyline` `create_circle` `create_arc` `create_point` `create_text` `create_mtext` `query_entities` `get_entity` `modify_entity` `move_entities` `copy_entities` `rotate_entities` `scale_entities` `delete_entities` |
| Blocks (6) | `list_blocks` `get_block_definition` `insert_block` `list_block_references` `set_block_attributes` `list_xrefs` |
| Annotation (7) | `extract_text` `create_dimension` `create_mleader` `list_dimensions` `list_leaders` `list_hatches` `list_tables` |
| Layouts & selection (8) | `list_layouts` `get_layout` `create_layout` `delete_layout` `set_current_layout` `list_viewports` `select_entities` `get_current_selection` |
| Sheets & plotting (7) | `create_viewport` `set_page_setup` `get_drawing_properties` `set_drawing_properties` `set_field_text` `plot_layouts_to_pdf` `plot_preview` |
| 3D solids (15) | `create_box` `create_cylinder` `create_sphere` `create_cone` `create_wedge` `create_torus` `create_pyramid` `extrude_profile` `revolve_profile` `sweep_profile` `loft_profiles` `boolean_solids` `slice_solid` `list_solids` `get_solid_properties` |
| Escape hatches (10) | `run_csharp` `run_lisp` `send_command` `cancel_command` `get_system_variable` `set_system_variable` `list_system_variables` `zoom_extents` `zoom_to_entities` `regen` |

**AutoCAD Architecture: 79 tools**

| Group | Tools |
|---|---|
| Core & property sets (12) | `list_aec_objects` `list_walls` `get_wall` `list_doors` `list_windows` `list_spaces` `list_schedule_tables` `get_property_sets` `add_property_set` `remove_property_set` `set_property_value` `list_property_set_definitions` |
| Openings (8) | `create_door` `create_window` `create_opening` `delete_opening` `set_opening_size` `list_door_styles` `list_window_styles` `list_opening_styles` |
| Spaces (6) | `create_space` `tag_space` `get_space` `set_space_properties` `list_spaces_detailed` `list_space_styles` |
| Slabs & roofs (9) | `create_slab` `create_roof_slab` `create_roof` `convert_roof_to_slabs` `set_slab_properties` `list_slabs` `list_roofs` `list_slab_styles` `list_roof_slab_styles` |
| Mass modelling (8) | `create_mass_element` `create_mass_group` `add_to_mass_group` `remove_from_mass_group` `set_mass_element_properties` `list_mass_elements` `list_mass_groups` `list_mass_shapes` |
| Grids & structure (10) | `create_column_grid` `create_layout_grid_2d` `create_layout_curve` `create_member` `set_member_properties` `list_column_grids` `list_layout_grids` `list_members` `list_member_styles` `list_member_node_shapes` |
| Assemblies (17) | `create_curtain_wall` `get_curtain_wall` `create_window_assembly` `get_window_assembly` `create_railing` `get_railing` `get_stair` `list_curtain_walls` `list_curtain_wall_styles` `list_curtain_wall_units` `list_curtain_wall_unit_styles` `list_window_assemblies` `list_window_assembly_styles` `list_railings` `list_railing_styles` `list_stairs` `list_stair_styles` |
| Sections & elevations (2) | `create_section` `create_elevation` |
| Fabrication & project options (7) | `clone_aec_style` `boolean_mass_elements` `create_member_profile` `create_mitered_member_path` `create_mvblock_from_solids` `edit_slab_openings` `create_project_construct` |

Arguments are documented in each tool's own description, which is what the model reads — names,
types, units, defaults and which arguments are mutually exclusive.

The [recoverable workflow tools](docs/workflow-tools.md) also advertise input schemas and guard
the active drawing's identity. Transactional authoring tools store operation receipts for safe
retries. See the [ACA modeling skill](skills/aca-modeling/SKILL.md), [API lessons](docs/aca-api-lessons.md),
and [native regression suite](tests/integration/README.md). For 2D drafting and sheet production
through `run_csharp` (annotative objects, viewports, title blocks, plotting), see the
[drafting lessons](docs/drafting-lessons.md).

> **Note on context cost.** Loading all 159 tool descriptions consumes substantial context.
> That is a real cost and there is currently no filtering mechanism. If you never touch ACA,
> that is most of the budget spent on tools you will not call.

---

## Supported versions

AutoCAD 2027 only, and this is a hard gate in three places at once: the `net10.0-windows`
target framework, the `AutoCAD.NET` 26.0.0 package, and `SeriesMin/Max="R26.0"` in the bundle
manifest. AutoCAD 2026 and earlier run on .NET 8, so supporting them is a retarget rather than
a version bump.

`AcadVersion` and `AcadNetPackageVersion` in `Directory.Build.props` are the two knobs; the
build fails loudly rather than silently resolving the wrong package if you set an unmapped
version.

### Installed somewhere non-standard?

The ACA and Roslyn references are found by probing `C:\Program Files\Autodesk\AutoCAD 2027`.
If AutoCAD lives elsewhere, pass it explicitly or the ACA tools and `run_csharp` are silently
compiled out:

```bash
dotnet build -p:AcadInstallDir="D:\Autodesk\AutoCAD 2027"
```

---

## Building and testing

```bash
dotnet build Nodal.AcadMcp.slnx -c Release
dotnet test tests/Nodal.AcadMcp.Tests
```

The tests deliberately reference only `Nodal.AcadMcp.Contracts`, so they run anywhere —
including a CI runner with no AutoCAD installed. They cover the admission control in front of
the code-execution tools; if you touch `HttpRequestGuard`, those tests are the contract.

The add-in compiles without AutoCAD installed, because the core API comes from the
`AutoCAD.NET` NuGet package. What it cannot do on such a machine is compile the **ACA tools**
or **`run_csharp`**: those references are probed from the AutoCAD install directory and are
compiled out when absent. So a CI build of the add-in is a genuine syntax and type check of
the non-ACA half, and proves nothing about the other 79 tools, which is exactly how the CI
workflow describes it.

To build the add-in as CI does, without touching your installed AutoCAD:

```bash
dotnet build src/Nodal.AcadMcp.Addin -p:AcadInstallDir="C:\NoSuchDir"
```

---

## Known limitations

- **No audit log.** Nothing records which tools ran or what code executed.
- **No document pinning.** Every tool targets whatever drawing is active *now*. If you switch
  tabs mid-sequence, subsequent calls retarget, and entity handles are per-database — so a
  handle can resolve to something unrelated in the other drawing.
- **No timeout on executed code.** A blocking snippet can wedge AutoCAD.
- **Unsigned assemblies.** In a locked-down CAD environment the bundle may refuse to load.
- **Most input schemas are permissive.** The workflow, drafting and preview tools declare
  strict schemas that reject unknown arguments. The older tools validate in the add-in, which
  returns errors naming the expected argument, but a misspelled *optional* argument to one of
  them is silently ignored.
- **PDF lettering in SHX fonts is linework.** `read_pdf_page` returns text only where the PDF
  used real fonts. A PDF plotted from AutoCAD with SHX fonts gives exact lines and angles but no
  readable numbers, so printed values still have to be read from an image of the page.
- **`check_drafting` compares boxes.** Annotation overlaps are tested on axis-aligned boxes, so
  rotated text can be flagged when its box overlaps without its letters touching.
- **One bridge per AutoCAD process.** A second instance takes an ephemeral port; the server
  picks the most recently started one.

---

## Disclaimer

**Use this at your own risk.** It is provided free, as-is, with no warranty of any kind and no
liability accepted for anything it does. That is the plain-English version of sections 7 and 8
of the [LICENSE](LICENSE), which are the terms that actually apply; by using, copying or
modifying this software you are doing so under that licence.

Specifically, and not as boilerplate:

- **It can damage or destroy your drawings.** It creates, modifies and deletes entities in
  whatever drawing is active. Some operations are not undoable. Work on copies, keep backups,
  and do not point it at anything you cannot afford to lose.
- **It executes arbitrary code by design.** `run_csharp`, `run_lisp` and `send_command` run
  with your full Windows privileges, inside your AutoCAD process. Read [SECURITY.md](SECURITY.md)
  before installing, not after.
- **A language model is driving it.** Models misunderstand instructions, make things up, and
  act confidently while wrong. Everything it produces needs checking by someone who knows what
  the answer should be.
- **You remain professionally responsible for your work.** If drawings touched by this tool
  become construction documents, permit sets or anything else people rely on, verifying them is
  yours to do. Nothing here transfers, shares or reduces the duty of care of the licensed
  practitioner who seals the work.
- **It is not fit for any particular purpose**, has not been validated against any standard or
  code, and carries no certification.
- **It is not an Autodesk product.** See [NOTICE](NOTICE). You need your own AutoCAD licence,
  and your use of AutoCAD remains governed by your agreement with Autodesk.

It is alpha software written by one person. Treat it accordingly.

## Licence

[Apache-2.0](LICENSE). See [NOTICE](NOTICE) for the Autodesk relationship — in short: this is
an independent, unofficial project, it redistributes no Autodesk code, and it requires your
own AutoCAD licence.
