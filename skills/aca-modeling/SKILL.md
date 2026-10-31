---
name: aca-modeling
description: Model and revise native AutoCAD Architecture casework, furniture, slabs and structural trim through Nodal AcadMcp. Use for multi-step ACA drawing work, project options, native geometry verification, or recovery after an interrupted operation.
---

# ACA modeling with Nodal AcadMcp

Establish the active DWG's full path, units, project and existing entity types before editing. Keep WCS coordinates separate from entity coordinates. A 10-foot dimension is 120 drawing units in an inch drawing; display formatting does not establish the units.

Use the typed workflow tools when available. Without MCP tools, call them through `Nodal.AcadMcp.Server.exe call <tool> @args.json`; never drive AutoCAD over COM. If the bridge is offline, preserve the plan and report the missing connection. Starting the bridge remains an explicit session action. After reconnecting, refresh tools and re-read drawing state; old handles alone do not prove the correct document is open.

## Read the source before modeling

Take values from what the drawings print: dimensions, angles, schedules, the site plan's bearings. When the PDF is vector, read its text and geometry rather than measuring a rendered image: an angle measured off pixels came out 0.05° off the printed value. Report what you measured, derived or interpreted (a window symbol, a foundation type, a missing dimension) so the user can check it.

## Work in recoverable stages

1. Read the source, identify retained elements, and record the intended transforms and dimensions.
2. Use `checkpoint_drawing` before substantial edits. Use a new checkpoint path for each stage; it saves the source and verifies the copy. Relative xrefs still depend on the original drawing directory.
3. For a design option, `create_project_construct` copies an existing saved construct within its current category. Verify the resulting project registration before editing the option.
4. Give each typed modeling operation a stable `operationKey`. Retry the same request with the same key after an uncertain response. If the key conflicts or its objects were erased, inspect the drawing before planning a replacement. Save receipts with a checkpoint.
5. Use `validate_model` and native previews to check each stage, then save and reopen a checkpoint before reporting completion. A queued command, successful compilation, or attractive screenshot alone is insufficient evidence.
6. Save after every stage, not only at the end: a hang or crash loses everything since the last save, and paper-space viewport edits can cause either. A drawing that has never been saved needs its real name first, because `checkpoint_drawing` works on a named drawing.
7. Keep every mutation in a replayable stage file, one-off settings included (a system variable, a style tweak, a purge). A change made only in an ad-hoc call is the one a rebuild misses.

## Choose native representations

- Casework: keep carcasses/fronts as mass elements with named styles and project layers. Use `boolean_mass_elements` for actual material subtraction; overlapping boxes do not create a cavity.
- Countertops: edit native slab holes with `edit_slab_openings`. Probe inside both the intended void and retained material. Check loop count and net area.
- Crown and trim: create a real member profile at all detail levels, then use a mitered member path. Profile X is left of travel, profile Y is up. Preserve path direction and baseline elevation deliberately.
- Furniture/appliances: use a distinct MV definition for the revised object. Inspect location, rotation and scale before replacing it. `create_mvblock_from_solids` converts input WCS solids into the reference frame; do not scale already full-size geometry a second time.
- Doors and windows: flush doors and plain window styles unless the source shows otherwise. When an elevation symbol is not clearly one of the styles you have, use a plain style and flag it.
- Roofs: where roof slabs meet at hips, valleys and ridges their edges are plumb and mitered; eave edges take the cut the design calls for (plumb, square or a set angle). Build the roof with `create_roof` and turn it into slabs with `convert_roof_to_slabs` (`eaveCut`, `eaveAngleDegrees`, `eaveEdgeStyle`); separate roof slabs come out with square edges everywhere. Tag spaces with `tag_space`.

## Verify geometry and communicate limits

Use type/style/layer checks, positive native volume, bounds and point probes together. `validate_model` with no `entities` inventories model space with counts by type and layer. Check casework, appliances and furniture against every window and door opening with `validate_model` and `openings`; an appliance placed across a window is easy to miss. WCS axis-aligned bounding-box clearance is only an axis separation check; it does not prove a lack of collisions or a functional door swing. Boundary probes are inconclusive: move them into the material or void. Unsupported checks must remain unresolved rather than being reported as passes.

Export native previews from known WCS directions. The preview tool restores view and selection; viewport pixel dimensions are not a scaled paper output. Inspect the PNG when appearance matters.

For `run_csharp` work, read [ACA API lessons](../../docs/aca-api-lessons.md). Materialize results inside the native transaction. Never return a lazy query over a side database that has already been disposed. Use synthetic fixtures from `tests/integration` to investigate an API uncertainty rather than experimenting in a user's project.

After a crash, distinguish saved state from the last successful in-memory operation. Reopen the last verified checkpoint at the appropriate xref base directory, inspect receipts and entity state, then resume only the missing stages. Do not replay an entire modeling script blindly. Replaying every stage file is right only when you have verified the drawing is at the state those files start from, for example a template with an empty model space and none of the stages' layers, styles or blocks. Replaying into a drawing that already holds some stages duplicates them.

For 2D drafting and sheet work (annotation, viewports, title blocks, plotting), read the [drafting lessons](../../docs/drafting-lessons.md).
