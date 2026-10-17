# ACA workflow API lessons

These patterns were extracted from native casework, countertop, appliance and crown modeling in ACA 2027. They are implementation notes, not evidence of the cause of a particular AutoCAD crash.

## Transaction and thread lifetime

AutoCAD API calls belong on the main thread. `run_csharp` now converts results **inside** the supplied transaction: deferred queries, `DBObject`s and `ObjectId`s must not be enumerated or dereferenced on the HTTP continuation after the transaction is disposed. The caller still owns any side databases it opens. Materialize their values before leaving their `using` scopes.

LISP callbacks convert object IDs while still on the AutoCAD callback thread. Long source is retrieved by a short tokenized callback instead of embedding the full source in `SendStringToExecute`. Evaluation is serialized. Timed-out callbacks are ignored and pending source is removed; a timeout cannot stop or roll back an expression already executing. Inspect state before retrying arbitrary code.

`ExecuteInCommandContextAsync` introduces AutoCAD's own command. Check for an existing user command before making that hop, then recheck drawing identity inside it. Observe asynchronous dispatch failures. For saves and PNG exports, use synchronous `Editor.Command` inside this context; a queued command is not a completed export.

## Native modeling

### Mass bodies

`Autodesk.Aec.Modeler.Body.Copy()` supplies independent boolean operands. `BooleanOperation` consumes inputs. Never pass a database-owned `MassElement.Body` directly. Transform copied bodies with each entity's `GeoEcs` into WCS, compute the result, then transform into the target's current ECS before `SetBody`.

A BRep mass needs `ChangeTo(ShapeType.BoundaryRepresentation)`, `SetBody`, a valid style and a database owner. Preserve the target style and layer. An empty subtraction is an error, not an invisible success.

### Structural profiles and miters

Adding display curves does not define a structural cross section. Create a closed polyline, call `Ring.AddSegmentsFrom(polyline, true)`, add the ring to a `Profile`, and call `MemberNodeShape.SetProfile` at **every** `DetailLevel`.

Clone an initialized Standard member style and edit its existing component. A new, incompletely initialized `MemberComponent` may not produce the intended member. Convert profile units once. For a horizontal baseline, positive profile X is left of travel and positive profile Y is up.

For two unit segment directions `a` and `b`, the shared miter plane normal is `a+b`. Express it separately in each member's local forward/left axes. Reject reversing corners. Extend each untrimmed baseline enough to cover the profile's lateral reach at that slope, then trim.

The start trim plane origin is relative to the extended start; the end trim plane origin is relative to the extended **end**. Both normals point forward. For an extension `p`, the local start plane is at `+p` and the end plane at `-p`. A fixed extension that happens to work for one crown profile is not general.

### Slabs and MV blocks

Slab holes are native topology: `AddHole`, `RemoveHole`, `UpdateSlab`, then verify `IsValid`, `Face.LoopCount` and `NetArea`. Point containment is evaluated in body coordinates: transform WCS probes with `GeoEcs.Inverse()`. `Body.Contains` returns a location enum; boundary results are not a conclusive inside/outside check.

MV block references can carry dimensional scale factors, not just `(1,1,1)`. Clone the definition so other references are unaffected. Populate its display representation view definitions and interference block. Convert WCS solid geometry into the reference's local frame to avoid applying location, rotation or scale twice. Verify the actual result under nonuniform scales rather than assuming the transformation succeeds.

## Checkpoints and receipts

`checkpoint_drawing` saves the active named document, copies its saved bytes to a temporary DWG, reopens that copy, checks requested handles, computes SHA-256, and publishes it under a new path. It does not package relative xrefs or make filesystem actions undoable. Keep checkpoints alongside the source or restore them to the source directory before resolving relative references.

Modeling operation receipts are Xrecords in the drawing's named objects dictionary. Geometry and receipt commit together. Receipt keys include the tool name; arguments are fingerprinted with canonical object-key ordering. Identical retries return the receipt, changed arguments fail, erased result objects fail. Receipts persist across reopening only after saving. They verify operation identity and handle existence, not that later edits left geometry unchanged.

`create_project_construct` uses `ProjectFile.CopyFrom` followed by `Project.AddConstruct(..., FileAddOption.Copy)`, scoped to the supplied APJ, and restricts copies to the same category/directory. This preserves relative xref resolution and avoids switching Project Navigator's current project. `Project.CopyFile` did not copy the synthetic construct in native tests. Verify the resulting registration and level/division assignments. Refuse unsaved source edits and existing DWG/XML destinations. Project operations are external effects, not database transactions; a failure requires inspection of any partial destination files.

New MV references do not yet have generated view instances. Calling `UpdateViewInstances` before the creating transaction commits can throw `eNotApplicable`; mark graphics modified and allow normal regeneration to generate the instances.

## Validation levels

- Compilation verifies signatures against the installed SDK, not native behavior.
- Pure tests cover schema validation, operation fingerprints, unit conversion and shared miter-plane math.
- Native scratch tests exercise actual ACA topology and drawing/file operations.
- A returned preview still needs visual inspection for presentation quality.
- Bounding-box separation is labeled as such; it is not an exact collision test.

Run native tests only in a new, empty, saved scratch drawing. The integration runner checks that precondition before making geometry. No project DWGs or screenshots belong in Git history.
