# Workflow validation status

## 0.3.0, native checks on ACA 2027, 2026-10-30

Each was run in a disposable ACA 2027 instance, started from the Aec Model (Imperial Ctb) template with no other AutoCAD open, through the deployed add-in and the server's command-line mode, with 15-second timeouts:

- Command-line mode: `ping`, `tools`, `tools <name>`, `call` with `@file`, `guide`, and the exit codes for a tool error and for no bridge. `GET /_guide` answers with the token and refuses without it. `NODALMCPSTART` and `NODALMCPSTATUS` print the connection note. Through MCP stdio, `initialize` returns the server instructions and a preview comes back as image content.
- `export_drawing_preview`: no arguments on an unsaved drawing; entities on a layer that is off left out of the framing; named views and vectors; a kept path; an unknown view and a wrong `expectedDocument` refused. Visual style and view restored after every call. With handles, Wireframe, Hidden, Conceptual, Shaded and Realistic each gave a blank image before the fix and a rendered one after.
- `validate_model`: inventory without `entities`, handle strings mixed with expectations, a failing expectation, and three malformed items refused. With `openings`, a box between the sill and head in front of a window was flagged; boxes below the sill and beside the opening were not.
- `create_roof`: a rectangle built directly, an L-shape through ROOFCONVERT, both at 30° with 120 in plate height and 18 in overhang. `convert_roof_to_slabs`: four and six slabs; every hip, valley and ridge edge plumb; eaves square, or plumb at +15°; rendered with clean hips and a valley.
- `create_slab` with `bulges` and from a polyline with an inward arc: one arc edge each, volumes 51,543 and 39,604 against 43,200 for the rectangle.
- `tag_space`: tags on the space's centroid, 39 by 32 in at 1/8" = 1'-0", and offset at 1/4" = 1'-0".
- `create_projection_symbol` (first and third angle), `create_slope_symbol` (roofs rising either way, and a level line refused), `create_center_mark`, `create_centerline`, checked on a plot.
- `check_drafting` on planted faults: one annotation overlap, one doubled line, the slivers beside a line 0.2 in off, and a line stopping 0.4 in short; a cleanly joined corner was not reported.
- `read_pdf_page` on a 10-page vector PDF: page 2 read at 24 by 18 in, with linework and the SHX-lettering note; the drawing held the same entities afterwards; page 99 refused with the page count. Line angles from the vectors matched the printed bearing to 0.001°.

The Python regression suite in `tests/integration` was not run for this release.

## Development validation on ACA 2027, 2026-09-26

- Full solution builds against the installed ACA SDK: zero errors; 12 existing Autodesk/reference and obsolete-API warnings.
- Add-in builds with ACA/Roslyn installation paths unavailable: zero errors. This confirms the new ACA handlers retain conditional compilation.
- All 48 pure tests pass, including argument rejection, schema serialization, operation fingerprints, units and shared miter-plane math.
- The reusable skill passes the skill validator; the native regression runner passes Python syntax compilation.

Native scratch tests passed long and malformed LISP handling, subsequent-call recovery, deferred native result enumeration, style cloning, identical operation replay, conflicting operation rejection, mass subtraction with expected volume and void/material probes, profile conversion, miter path creation, and adding/removing slab holes with expected loop counts and net area.

Native testing exposed two issues that were corrected: the command-context wrapper itself made a busy check reject checkpoints, and eagerly regenerating a new MV reference threw `eNotApplicable`. A separate synthetic project test confirmed `ProjectFile.CopyFrom` plus `AddConstruct(..., Copy)` works; `Project.CopyFile` did not copy that fixture.

## Still required before release

The complete native suite has **not** passed on the final revision. The test host produced an error report during shutdown, and later desktop automation could not activate its window. The shutdown cause has not been established. No crash data was uploaded and no existing project drawing was used as a fixture.

Run `tests/integration/workflow_regression.py` in a fresh, accessible ACA session, including its optional project fixture. In particular, verify the corrected checkpoint/save/readback path, PNG export and view restoration, MV replacement with nonuniform scale, and the complete typed project-copy operation. Inspect the exported PNG. Reopen the saved drawing and verify operation receipts before repeating an interrupted mutation. Test cancellation and a clean session shutdown as well.

Do not present these changes as a confirmed fix for a particular AutoCAD crash. Keep release/deployment separate from this development validation until the remaining native checks pass.
