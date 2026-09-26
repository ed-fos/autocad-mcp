# Workflow validation status

Development validation on ACA 2027, 2026-09-26:

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
