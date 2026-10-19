# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

AutoCAD compatibility is not encoded in the version number — it is in the release tag and the
asset name (e.g. `v0.1.0` / `Nodal.AcadMcp-acad2027-*.zip`), because supporting a new AutoCAD
year is a retarget rather than a feature bump.

## [Unreleased]

### Added
- Ten tools for verified drawing checkpoints, native previews, model validation, project
  construct copies, style cloning, mass booleans, structural profiles and miters, MV blocks
  from solids, and native slab openings.
- Bounded input schemas for the new workflows, drawing identity guards, and persisted
  operation receipts for transactional authoring retries.
- A reusable ACA modeling skill, API lessons, pure regression tests and an opt-in native
  scratch-drawing regression suite.
- Seven sheet-production tools, so a drawing set no longer needs `run_csharp` for paper space:
  - `create_viewport` builds floating viewports from a named model view or a model point, at a
    scale given as `1/4" = 1'-0"`, `1:48` or a factor, with lock, layer and annotation scale. It
    enforces the call pattern that avoids AutoCAD 2027's paper-space hang and crash: the layout
    switch, the creation (viewports off) and turning them on each get their own commit, and it
    never erases anything. It is classed `tool-defined` for undo and reports its commits.
  - `set_page_setup` sets device, media (canonical or display name), plot area, scale, rotation,
    centring or origin, plot style table and lineweight options on one or more layouts, and
    returns the settings before and after (templates often carry a 90° rotation and an offset
    origin).
  - `plot_layouts_to_pdf` writes one PDF per sheet from a file-name pattern and/or one combined
    set, in the foreground, restoring `BACKGROUNDPLOT` and the current layout afterwards.
  - `plot_preview` plots a layout, a window of it, or a model-space window and returns a PNG at a
    chosen pixel size and crop, with plot styles and lineweights. The PDF is rasterised in-process
    with Windows' own PDF renderer; no extra package.
  - `get_drawing_properties` / `set_drawing_properties` read and write DWGPROPS, custom
    properties included, and re-evaluate fields; `set_field_text` puts a field expression such
    as `%<\AcVar CustomDP.ProjectName>%` into MText, text or attributes and reports whether it
    resolved.
- The MCP server passes an `image` in a tool result on as image content, so the model can look at
  a preview.

### Changed
- An argument supplied with the wrong JSON type is now a `bad_argument` error for **optional**
  arguments too, instead of silently falling back to the default (for example `"write": "false"`
  as a string used to commit a `run_csharp` write). Wrong-typed items in arrays and point
  coordinates are reported by index instead of being dropped, so `[10, "20", 0]` no longer
  becomes the point (10, 0, 0). Integral numbers such as `50.0` are accepted where an integer is
  expected.
- A request body that is valid JSON but not an object is rejected as `bad_json`.

### Fixed
- `list_viewports`, `get_layout` and every viewport read-back returned only `{handle, error:
  "eNotImplementedYet"}` on AutoCAD 2027, because `Viewport.PlotStyleSheet` throws there for every
  viewport. That one property is now guarded, so the rest of each viewport's state comes back.
- A string argument sent as an object now says what it got, and recognises the shape Windows
  PowerShell's `ConvertTo-Json` produces for `Get-Content` output (`{"value": ..., "PSPath": ...}`)
  with a hint naming the fix.
- Attribute values and property-set text values given as objects or arrays are rejected instead
  of being stored as their raw JSON.
- A request body over the bridge's 16 MB limit now gets HTTP 413 with a `payload_too_large` error
  giving the size sent and the limit, instead of the connection closing with no reply ("The
  underlying connection was closed unexpectedly"). A malformed `Content-Length` is a 400
  `bad_request` instead of being read as zero. A caller without the session token still gets the
  same terse 403 whatever it sent, and a refused body is now drained rather than read into memory.
- Convert `run_csharp` results before the native transaction and main-thread scope end.
- Retrieve long LISP programs through a short callback token; serialize evaluations,
  convert native return values on the callback thread, and discard late timeout callbacks.
- Propagate command-context dispatch failures and keep a cached offline diagnostic tool
  useful after the bridge reconnects.

### Documented
- `run_csharp`: two AutoCAD 2027 paper-space viewport failures, both reproduced through the tool,
  with the call pattern that avoids them. Making a layout current in the same call that erased
  viewports already on it hangs AutoCAD; setting `Viewport.On = true` on a viewport created in
  the same call crashes it intermittently when the call commits.

## [0.1.0] — first release

Initial release. 133 tools across AutoCAD and AutoCAD Architecture.

### Added
- 63 AutoCAD tools: entities, blocks, annotation, layouts and selection, 3D solids, and the
  `run_csharp` / `run_lisp` / `send_command` escape hatches.
- 70 AutoCAD Architecture tools: walls, doors, windows, openings, spaces, slabs, roofs, mass
  elements and groups, column grids, layout grids, structural members, curtain walls, window
  assemblies, railings, stairs, property sets, and generated sections and elevations.
- Per-session authentication on the in-AutoCAD bridge, with `Host` / `Origin` /
  `Content-Type` validation against browser CSRF and DNS rebinding.
- Undo classification (`single-step` / `tool-defined` / `not-undoable`) surfaced to the model,
  so a tool that is not one Ctrl+Z is never advertised as one.
- Automatic instance discovery: the MCP server locates a running AutoCAD through a per-user
  instance file, so start order does not matter and a second AutoCAD takes an ephemeral port
  instead of silently losing the bridge.
- Test suite covering the admission control in front of the code-execution tools.

### Security
- The bridge is opt-in per session: the add-in loads at AutoCAD startup so its commands and
  ribbon exist, but opens no socket until you click **Start** or type `NODALMCPSTART`.
- `set_system_variable` refuses `SECURELOAD`, `TRUSTEDPATHS`, `TRUSTEDDOMAINS`,
  `ACADLSPASDOC`, `LISPSYS`, `AUTOLOAD` and `SECUREREMOTEACCESS`, which govern what AutoCAD
  will load and persist beyond the session.
- See [SECURITY.md](SECURITY.md) for the threat model, including support-path persistence.
