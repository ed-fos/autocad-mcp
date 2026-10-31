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

## Building models

Measured while modelling small buildings in ACA 2027 (walls, openings, foundation, slabs, a hip roof, spaces, casework, furniture, site), mostly through `run_csharp`.

### Styles, materials and content

- Import styles, materials and MV definitions from the shipped libraries (`C:\ProgramData\Autodesk\ACA 2027\enu\Styles\Imperial\*.dwg`; MV content under `...\AEC Content\Imperial\Design\`): `ReadDwgFile` into a side database, then `WblockCloneObjects(ids, destinationDictionaryId, map, DuplicateRecordCloning.Ignore, false)` from the matching `AEC_*` sub-dictionary of the named-objects dictionary. Referenced materials and profiles come along.
- Much of the vehicle and planting content is plan-only. `Sports Car 3D` and `3D Tree - Round (AUS)` have model views.
- `new DictionaryXxxStyle(db).CloneEntry(record, "Name")` returns the new style's id. A name containing `/` fails with `eInvalidInput`. There is no managed `DictionaryCleanupGroupDefinition`.
- Materials live on style components: `WallStyleComponent.MaterialId`, the components of `SlabStyle` and `RoofSlabStyle` (reached by reflection on `Components`), `MassElementStyle.MaterialId` and `SlabEdgeStyle.FasciaMaterialId`. `Entity.GetMaterialComponents` returns nothing for walls, so a per-object override does not help: clone the style.

### Walls and openings

- **Anchoring an opening (cost hours).** Open the target wall `ForWrite` in the same transaction before `SetAnchor`, or `SetAnchor` silently does nothing. Assign `anchor.CurveId = wallId` after `SetAnchor`, never before. `SetAnchor` adds the anchor to the database itself, so `AddNewlyCreatedDBObject` on it throws `eNotInDatabase`. The order is `SubSetDatabaseDefaults`, `SetToStandard`, `SetAnchor`, `CurveId`. `AnchorId` and `IsAnchoredToWall` read "not anchored" until the transaction commits, so check `anchor.CurveId` instead. `create_door`, `create_window` and `create_opening` do all of this.
- **Justification.** In the Imperial template the Standard style's Baseline justification hangs the whole wall on one side of its baseline; in the metric template it is centred. Measure `GeometricExtents` after the commit, because mid-transaction a new wall's extents are the bare baseline. For a path traced with the interior on the left of travel, `Left` put the body outside. Anchored openings follow when the justification changes.
- **Doors and windows.** `create_door` takes `flipHinge` and `flipSwing`; check them in a 2D wireframe top view, where the plan representation shows. A window whose sill is above the 42" cut plane does not show in plan.
- **An opening's frame.** `GeoEcs` of a door or window has X along the wall from the opening's start, Y across the wall and Z up, with its origin at `Location`, which sits on the wall at sill height. `Width`, `Height` and `SillHeadHeightUtility` give the rest. Its `GeometricExtents` are the plan representation, a few inches deep and flat, so they say nothing about the opening in 3D. `validate_model` with `openings` builds each opening's clear zone in this frame.

### Slabs and roof slabs

- Slab boundary points are the top surface and the thickness grows down. `BaseThickness` is the settable property; `Thickness` is a method.
- `AddHole` on a slab created in the same transaction throws `eInvalidInput`. Create the slab, commit, and cut holes in a later call.
- `RoofSlab.SetBaseCurve(points, z, true)` with `PivotPoint = points[0]` and `Slope` rotates the slab about its first edge and keeps the polygon's true length, so give the polygon in slope length (plan depth / cos(slope)) for it to land on the plan. The base face is the top.
- **Roof slab edges.** Where roof slabs meet at hips, valleys and ridges, their edges are plumb and mitered to each other. The eave edges take whatever cut the design calls for: plumb, square to the slab, or a set angle. A slab's edges all start square. Set a cut on a copy of the face and write it back: `var f = rs.Face; f.EdgeAt(i).SetCutOption(square); f.EdgeAt(i).Angle = radians; rs.SetBaseFaceEcs(f)`. Edge `i` runs from boundary vertex `i` to `i + 1`.
- **Edge ids are not indices.** `MiterWith(other, edgeA, edgeB)`, `IsEdgeCurveArc`, `ExtendEdgeTo` and the other `Int16` edge arguments take a `SlabEdge.Id` (1026 and up on a new slab), not an index. A four-slab hip roof built with indices got `eInvalidInput` from every `MiterWith`, and `IsEdgeCurveArc(1)` with an index **crashed AutoCAD** (an access violation in the .NET runtime), although its documentation says an invalid id returns false.
- **A hip roof as slabs.** Let an ACA Roof solve the hips, valleys and ridges, then build one roof slab per roof face: `roof.RoofBodyAt(i).GetRoofFace(ExteriorFace).Face` is the face's top surface as a 3D polygon (`InteriorFace` is the underside, at plate height on the wall line), and `SetBaseCurve(polygon, face normal, true)` puts the slab exactly on it, growing down by `BaseThickness`. Keep the body alive while its face is in use and copy the points out at once. The Roof's own eaves are square cut: the top runs past the underside by thickness x sin(slope). `convert_roof_to_slabs` does this, with every hip, valley and ridge edge plumb and the eaves cut as asked.
- **Building a Roof through the API.** A roof's profile has to be its own (`roof.RoofProfile`): one made with `new` throws on `Segments.Add` and on `ConvertPolylineToRoofContour`. Even its own profile only takes a polyline with four vertices, the count it starts with; any other count throws `eNullObjectPointer`, and `Segments.Add` is refused. Segments edited through `SegmentAt(i)` (`SetPeaked`, `SetOverhang`) keep their changes. So `create_roof` builds four-edge roofs directly and anything else with `(command "_.ROOFCONVERT" polyline "" "_Yes")`, which stays on the command line, then sets each segment. `ROOFSLABCONVERT` opens a dialog, so a tool cannot drive it.
- Fascia: `var f = rs.Face; f.SetEdgeStyleIdAt(0, edgeStyleId); rs.SetBaseFaceEcs(f)`. The `Fascia - 1x8` profile spans X 0..1 and Y 0..8; `Extent()` returned its minimum and maximum with Y swapped.
- **Arc edges.** `SetBaseCurve` takes straight edges. `ConvertToArcAt(edgeIndex, pointOnArc)` (an index this time, and the point in the slab's own coordinates) turns one into an arc afterwards; it works in the transaction that created the slab. `create_slab` does this for a polyline's bulges or a `bulges` list.

### Spaces and tags

- `create_space` works. Names are normalised to the space style's name list, in title case. `CentroidWCS(true)` is the true WCS centroid.
- A space separator is a plain `Line` plus `Autodesk.Aec.DatabaseServices.Utility.SetAutomaticallyBoundSpaces(line, new ObjectIdCollection(), AutomaticSpaceBoundary.Yes)`.
- **Room tags.** The `Aec8_Room_Tag` MV block, which the ACA model templates include, shows the SpaceObjects Name and Number. Create a `MultiViewBlockReference`, then an `AnchorExtendedTagToEntity` (`Autodesk.Aec.PropertyData.DatabaseServices`): `SetAnchor` first, then `ReferencedEntityId`. The anchor treats the tag's `Location` as an offset from the space's `Location`, so set `Location = origin + (centroid - space.Location)` before anchoring. The reference is annotative and gets no scale context of its own: without one it shows at definition size (about 4 in wide); with the 1/8" = 1'-0" context it is 39 in wide, the definition times the annotation plot size (3/32") times 96. `SetDefaultLayer` throws `eNotImplementedYet` on it. `SpaceObjects:Number` takes an integer. `tag_space` does all of this.
- A space renders as a solid box in shaded views. Freeze its layer, or make it transparent, before rendering.

### Mass elements

- `MassElement.Create(ShapeType)`, `SetDatabaseDefaults`, `SetToStandard`, append, and only then set `Location`, `Width` (X), `Depth` (Y), `Height`, `Radius`, `Normal` and `Rotation`; geometry set before the append throws. `Location` is the centre of the bottom face for boxes and cylinders.
- A B-rep mass: build a `Body` (`Body.Box`, `Body.Add`, or `Body.ConvexHull` of points centred on their bounding-box centre), `MassElement.Create(ShapeType.BoundaryRepresentation)`, append, `SetBody(body, false)`, then `Location = centre`. `SetBody(body, true)` recentres the body without moving the element, so every piece lands at the origin.
- Extents of the plan representation of slabs and mass elements are degenerate. Read `MassElement.Body` or the component bodies for real geometry.

### Elevations and sections

- Build them in WCS, never the current UCS: `BuildingElevationLine.SetVertices(points, new Plane(origin, Vector3d.ZAxis))`. A UCS-derived plane throws, and a vertical plane silently gives a plan projection for one of the axes. `create_elevation` and `create_section` pin the UCS to World while generating.
- The elevation line looks to the left of its direction of travel: walk it with the wall on your left.

### Rendering

- `PNGOUT` given a selection renders it only in 2D wireframe. Measured in AutoCAD 2027 on a box viewed from the south-west: with the box selected, Wireframe, Hidden, Conceptual, Shaded and Realistic each gave an image of one flat colour, and 2D wireframe gave the box. With nothing selected (Enter at "Select objects") every style exported what the view showed. A recipe that works in any style: a `ViewTableRecord` (parallel projection, `Target`, `ViewDirection`), then `(command "_.-VIEW" "_R" name)`, `(command "_.-VISUALSTYLES" "_C" "Realistic")`, `ZOOM _E`, and `(setvar "FILEDIA" 0) (command "_.PNGOUT" path "")`. The preview tool now takes `visualStyle`, still frames on any handles, and exports the selection only in 2D wireframe. ZOOM Extents does not work in a perspective view.
- `ViewTableRecord.CenterPoint` is in DCS relative to the WCS origin, not to `Target`, so two named views that differ only in `Target` restore to the same view. Follow `-VIEW _R` with `(command "_.ZOOM" "_C" "x,y" height)`.
- `VPOINT` through `SendStringToExecute` intermittently drops to plan, and `Editor.SetCurrentView` from application context ignores `ViewDirection` and loses the visual style.
- AEC objects render grey in Realistic, from their materials; Shaded shows ByLayer colours.
- With a SpaceMouse connected, AutoCAD runs `SPACEMOUSEACTION` for a moment after a view change. `run_lisp` waits up to 2 s for it instead of answering `busy`.

### Choices to flag rather than make silently

- **Doors and windows:** do not add detail the source does not show. Use plain styles (flush doors, a simple casement or fixed window) unless the drawings show panels or a specific operation.
- **Window types:** elevation opening symbols vary between offices, and a pivot window's symbol is easily misread as a louvre. When a symbol is not clearly one of the styles you have, use a plain style and flag it.
- **Clearances:** check casework and appliances against every opening's width, sill and head (`validate_model` with `openings`); an appliance across a window is easy to place and easy to miss.
- **Foundation:** footings can be slabs, walls with a footing component, or mass elements. Ask which the project uses.
- **Angles:** take a building's rotation from the angle printed on the site plan, not from a measurement. See [Reading the source drawing](drafting-lessons.md#reading-the-source-drawing).

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
