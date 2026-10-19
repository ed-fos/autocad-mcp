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
- `cancel_command`: cancels a command left waiting at a prompt. It queues Ctrl+C twice with no
  Enter after it, then polls `CMDACTIVE` until it reads 0, re-sending for nested commands.
  `send_command` could not do this (see Changed).
- `run_csharp` takes an optional `prelude`: helper code compiled into the same method ahead of
  `code`, so a helper library no longer has to be pasted onto every snippet. Errors number
  prelude lines separately.
- `run_csharp` imports `Autodesk.AutoCAD.PlottingServices` by default. `PlotType` stays the
  `DatabaseServices` type through a using alias, so existing page-setup snippets still compile.
  With ACA loaded, `ContentType` now means the AutoCAD (MLeader) type instead of being an
  ambiguous reference (CS0104).
- [Drafting lessons](docs/drafting-lessons.md), the 2D counterpart of the ACA API lessons:
  annotative order of operations, multileader vertices, hatch pattern order, title-block fields,
  plotting to PDF, PDF import, viewport failures and crash-recovery practice.
- README: a raw HTTP fallback for driving the bridge without an MCP client, and a
  troubleshooting row for a server that is registered but shows no tools in Claude Code.
- Annotative drawings: `create_text`, `create_mtext`, `create_dimension` and `create_mleader` take
  `annotative` (default: the style's setting) and `annotationScale` (a scale name such as
  `1/4" = 1'-0"` or a factor such as `48`; default the current CANNOSCALE in model space, 1:1 in
  paper space), and sizes as plotted (`paperHeight`, and `paperWidth` for MText). An annotative
  object gets exactly that one scale context and comes out at plotted size × scale. `insert_block`
  gives an annotative block reference its scale context before creating its attributes, and each
  annotative attribute the same scale (a new attribute otherwise takes the current one, so a block
  inserted at another scale showed without it), so both come out at the right size in the same
  viewports. Results report the scale and the sizes used.
- `create_mleader`: `vertices` for bent leaders (bend points from the arrow toward the text) and
  `textAngle` (`horizontal` keeps the text level whatever the leader's slope).
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
- `run_csharp` runtime errors say where they happened. The `script_error` message names the
  library call that threw and the snippet line, e.g. `eInvalidInput (thrown by
  Hatch.set_PatternAngle, called from Ha() at prelude line 30, reached from line 12)`. `detail`
  lists the frames. It used to be the bare message and the exception type. Snippets are compiled
  with a portable PDB and `#line` directives so frames carry snippet lines. Compile errors in
  `usings` or after the snippet's last line are now labelled as such instead of getting a
  miscounted line number.
- `send_command` with `mode: 'command'` is refused with `unsupported_mode` and an explanation.
  It used to fail with a bare `eInvalidInput`: `Editor.Command` needs a command context, and
  tool calls never run in one. The default mode no longer attempts it first.
- `send_command` refuses cancel keystrokes (`^C`, `^C^C`, Ctrl+C or Esc characters) and points to
  `cancel_command`. Caret notation was typed literally, and a real Ctrl+C was followed by the
  Enter that ends every queued line. Verified in AutoCAD 2027: with LINE pending, Ctrl+C twice
  plus Enter cancelled LINE and then restarted ZOOM, the last command, which is the `'ZOOM` a
  stuck `-INSERT` turned into in the drawing session. The same bytes without the Enter cleared it.
- `run_lisp` says when the expression left a command waiting at its prompt. A `(command ...)`
  given too few inputs used to return a plain success while every later call answered `busy`.
  After the expression returns, the add-in reads `CMDACTIVE` and `CMDNAMES` on the next idle
  tick; if a command is active the reply adds `commandState: "pending"`, `pendingCommand` (for
  example `"-INSERT"`), `cmdActive` and a hint to call `cancel_command`. A `lisp_error` carries
  the same hint as its `detail`. The read waits at most 1 s, because a drag prompt may hold off
  AutoCAD's idle loop; past that the reply says `commandState: "unknown"` instead of hanging.
  An idle command line adds nothing to the reply. Verified in AutoCAD 2027 with LINE and
  `-INSERT` left pending.

### Removed
- The `Application.Invoke` implementation of `run_lisp` in `EscapeTools`. It was always replaced
  at registration by the `LispBridge` one, could not reach AutoLISP, and its description wrongly
  said `(command ...)` fails from `run_lisp`.

### Fixed
- Annotative styles gave wrong sizes. AutoCAD does not carry a style's annotative flag over to
  DBText, MText or dimensions made through the API, so they came out non-annotative, and 3/32"
  text was 3/32" tall in model space at 1/4" = 1'-0" instead of 4.5". An annotative block
  reference had no scale context, so it and its attributes showed at definition size.
- `create_mleader` without `textHeight` used TEXTSIZE, not the style's text height its
  description promised.
- `insert_block` reports the reference's actual scale factors; an annotative block's include the
  annotation scale (`scale` 2 at 1/4" = 1'-0" is 96).
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
  viewports already on it hangs AutoCAD. Setting `Viewport.On = true` on a viewport created in
  the same call crashes it intermittently, either when the call commits or at a later
  `LayoutManager.CurrentLayout` switch in the same call. The description also lists the snippet
  pitfalls the drawing session hit: `doc` is a parameter (CS0136), and
  `DBDictionary.Cast<DBDictionaryEntry>()` throws once enumerated.
- `run_lisp`: `(command ...)` works, because the expression runs at the drawing's command line. The
  description now says so and gives its limits: a command left waiting for input stays at its
  prompt (use `cancel_command`), and a dialog or `(pause)` waits for a person.
- Contributing and the native regression README: a second AutoCAD on the same desktop is not
  isolated from a hang in the first. Close every other AutoCAD first, use 15 s timeouts, and kill
  the test instance on timeout.

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
