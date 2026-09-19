# Nodal.AcadMcp

An MCP server that lets a language model drive **AutoCAD 2027** and **AutoCAD Architecture** —
133 tools covering entities, blocks, annotation, layouts, 3D solids, and the full ACA object
model: walls, doors, windows, spaces, slabs, roofs, mass elements, column grids, structural
members, curtain walls, stairs, railings, and generated sections and elevations.

> **Status: alpha.** This is one person's tool, published in case it is useful. It works, it
> is used daily against real drawings, and it has rough edges. There is no support promise.

> **⚠️ Read [SECURITY.md](SECURITY.md) before installing.** This puts an endpoint inside your
> running AutoCAD and three of its tools execute arbitrary code. That is deliberate and it is
> defensible, but it is not something to install casually.

---

## Why not the Autodesk one?

AutoCAD 2027 ships its own first-party MCP server, and if it covers what you need, **use
that** — it is supported, signed, and installed already.

This exists because it covers ground the first-party server does not:

| | Autodesk's MCP | Nodal.AcadMcp |
|---|---|---|
| Tools | 9 | 133 |
| AutoCAD Architecture | none | 70 tools across walls, doors, windows, spaces, slabs, roofs, masses, grids, members, assemblies, sections |
| 3D solids | no | 15 tools (primitives, booleans, extrude/revolve/loft/sweep, slice) |
| Escape hatches | no | `run_csharp`, `run_lisp`, `send_command` |
| Supported | yes, by Autodesk | no |
| Signed | yes | no |

If you work in ACA, that second row is the entire reason this repository exists.

---

## Requirements

- **AutoCAD 2027** (R26.0). Not 2026 or earlier — see [Supported versions](#supported-versions).
- **AutoCAD Architecture 2027** for the 70 ACA tools. Everything else works on vanilla AutoCAD;
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

```
NODALMCPSTART
```

**This does not happen automatically, on purpose.** The bridge executes arbitrary code, so it
is opt-in per session. `NODALMCPSTATUS` reports whether it is listening; `NODALMCPSTOP` closes
it and invalidates the session token.

If you genuinely want it up on every start, put `NODALMCPSTART` in your `acaddoc.lsp`.

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
| `Unknown command "NODALMCPSTART"` | The bundle did not load. Check it is at `%APPDATA%\Autodesk\ApplicationPlugins\Nodal.AcadMcp.bundle` and that the zip was unblocked before extracting. As a fallback, `NETLOAD` the add-in DLL directly. |
| Only `acad_bridge_unavailable` is listed | The bridge is not open in any AutoCAD. Run `NODALMCPSTATUS`. |
| Tools still missing after `NODALMCPSTART` | Your client cached the tool list. Reconnect the server. |
| `403` / "session token is stale" | The bridge restarted. Retry; the server re-reads the token per request. |
| ACA tools return `aca_unavailable` | Either ACA is not loaded in this session, or the add-in was built on a machine without ACA installed. |

---

## How it works

```
MCP client  ──stdio──▶  Nodal.AcadMcp.Server.exe  ──HTTP/loopback──▶  add-in inside acad.exe
                        (thin proxy, owns no                          (owns all 133 tools,
                         tool definitions)                             marshals to main thread)
```

The server is deliberately thin: at list-time it asks the add-in what tools exist and forwards
the answer. **Adding a tool to the add-in needs no server change and no redeploy of the
server.**

Every request carries a per-session token; the add-in also checks `Host` and rejects anything
carrying an `Origin`. See [SECURITY.md](SECURITY.md) for why all of that is necessary for a
loopback port.

The add-in marshals every call onto AutoCAD's main thread via `Application.Idle` — touching
the AutoCAD API from a worker thread is an immediate hard crash, so this is not optional.

### Undo honesty

Each tool declares what `Ctrl+Z` actually takes back, and the server turns that into the first
thing the model reads:

- `single-step` — the whole call commits as one undo step (every typed tool)
- `tool-defined` — `run_csharp`, `run_lisp`, `send_command`: reversibility is whatever your
  code produced, and side effects outside the drawing are not undoable at all
- `not-undoable` — `set_system_variable` writes profile state that is not on the undo stack

This matters more than it sounds: a model that believes a call is one keystroke from being
undone will try it speculatively on a live drawing.

---

## Tool catalog

**Plain AutoCAD — 63 tools**

| Group | Tools |
|---|---|
| Session (3) | `acad_status` `list_documents` `list_layers` |
| Entities (15) | `create_line` `create_polyline` `create_circle` `create_arc` `create_point` `create_text` `create_mtext` `query_entities` `get_entity` `modify_entity` `move_entities` `copy_entities` `rotate_entities` `scale_entities` `delete_entities` |
| Blocks (6) | `list_blocks` `get_block_definition` `insert_block` `list_block_references` `set_block_attributes` `list_xrefs` |
| Annotation (7) | `extract_text` `create_dimension` `create_mleader` `list_dimensions` `list_leaders` `list_hatches` `list_tables` |
| Layouts & selection (8) | `list_layouts` `get_layout` `create_layout` `delete_layout` `set_current_layout` `list_viewports` `select_entities` `get_current_selection` |
| 3D solids (15) | `create_box` `create_cylinder` `create_sphere` `create_cone` `create_wedge` `create_torus` `create_pyramid` `extrude_profile` `revolve_profile` `sweep_profile` `loft_profiles` `boolean_solids` `slice_solid` `list_solids` `get_solid_properties` |
| Escape hatches (9) | `run_csharp` `run_lisp` `send_command` `get_system_variable` `set_system_variable` `list_system_variables` `zoom_extents` `zoom_to_entities` `regen` |

**AutoCAD Architecture — 70 tools**

| Group | Tools |
|---|---|
| Core & property sets (12) | `list_aec_objects` `list_walls` `get_wall` `list_doors` `list_windows` `list_spaces` `list_schedule_tables` `get_property_sets` `add_property_set` `remove_property_set` `set_property_value` `list_property_set_definitions` |
| Openings (8) | `create_door` `create_window` `create_opening` `delete_opening` `set_opening_size` `list_door_styles` `list_window_styles` `list_opening_styles` |
| Spaces (5) | `create_space` `get_space` `set_space_properties` `list_spaces_detailed` `list_space_styles` |
| Slabs & roofs (8) | `create_slab` `create_roof_slab` `create_roof` `set_slab_properties` `list_slabs` `list_roofs` `list_slab_styles` `list_roof_slab_styles` |
| Mass modelling (8) | `create_mass_element` `create_mass_group` `add_to_mass_group` `remove_from_mass_group` `set_mass_element_properties` `list_mass_elements` `list_mass_groups` `list_mass_shapes` |
| Grids & structure (10) | `create_column_grid` `create_layout_grid_2d` `create_layout_curve` `create_member` `set_member_properties` `list_column_grids` `list_layout_grids` `list_members` `list_member_styles` `list_member_node_shapes` |
| Assemblies (17) | `create_curtain_wall` `get_curtain_wall` `create_window_assembly` `get_window_assembly` `create_railing` `get_railing` `get_stair` `list_curtain_walls` `list_curtain_wall_styles` `list_curtain_wall_units` `list_curtain_wall_unit_styles` `list_window_assemblies` `list_window_assembly_styles` `list_railings` `list_railing_styles` `list_stairs` `list_stair_styles` |
| Sections & elevations (2) | `create_section` `create_elevation` |

Arguments are documented in each tool's own description, which is what the model reads — names,
types, units, defaults and which arguments are mutually exclusive.

> **Note on context cost.** 133 tool descriptions is roughly 15–18k tokens on every session.
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
the non-ACA half, and proves nothing about the other 70 tools — which is exactly how the CI
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
- **Input schemas are permissive.** Arguments are validated in the add-in, which returns
  errors naming the expected argument, rather than being declared as JSON Schema up front.
  A misspelled *optional* argument is currently silently ignored.
- **One bridge per AutoCAD process.** A second instance takes an ephemeral port; the server
  picks the most recently started one.

---

## Licence

[Apache-2.0](LICENSE). See [NOTICE](NOTICE) for the Autodesk relationship — in short: this is
an independent, unofficial project, it redistributes no Autodesk code, and it requires your
own AutoCAD licence.
