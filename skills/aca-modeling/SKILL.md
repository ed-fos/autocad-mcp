---
name: aca-modeling
description: Model and revise native AutoCAD Architecture casework, furniture, slabs and structural trim through Nodal AcadMcp. Use for multi-step ACA drawing work, project options, native geometry verification, or recovery after an interrupted operation.
---

# ACA modeling with Nodal AcadMcp

Establish the active DWG's full path, units, project and existing entity types before editing. Keep WCS coordinates separate from entity coordinates. A 10-foot dimension is 120 drawing units in an inch drawing; display formatting does not establish the units.

Use the typed workflow tools when available. If the bridge is offline, preserve the plan and report the missing connection. Starting the bridge remains an explicit session action. After reconnecting, refresh tools and re-read drawing state; old handles alone do not prove the correct document is open.

## Work in recoverable stages

1. Read the source, identify retained elements, and record the intended transforms and dimensions.
2. Use `checkpoint_drawing` before substantial edits. Use a new checkpoint path for each stage; it saves the source and verifies the copy. Relative xrefs still depend on the original drawing directory.
3. For a design option, `create_project_construct` copies an existing saved construct within its current category. Verify the resulting project registration before editing the option.
4. Give each typed modeling operation a stable `operationKey`. Retry the same request with the same key after an uncertain response. If the key conflicts or its objects were erased, inspect the drawing before planning a replacement. Save receipts with a checkpoint.
5. Use `validate_model` and native previews to check each stage, then save and reopen a checkpoint before reporting completion. A queued command, successful compilation, or attractive screenshot alone is insufficient evidence.

## Choose native representations

- Casework: keep carcasses/fronts as mass elements with named styles and project layers. Use `boolean_mass_elements` for actual material subtraction; overlapping boxes do not create a cavity.
- Countertops: edit native slab holes with `edit_slab_openings`. Probe inside both the intended void and retained material. Check loop count and net area.
- Crown and trim: create a real member profile at all detail levels, then use a mitered member path. Profile X is left of travel, profile Y is up. Preserve path direction and baseline elevation deliberately.
- Furniture/appliances: use a distinct MV definition for the revised object. Inspect location, rotation and scale before replacing it. `create_mvblock_from_solids` converts input WCS solids into the reference frame; do not scale already full-size geometry a second time.

## Verify geometry and communicate limits

Use type/style/layer checks, positive native volume, bounds and point probes together. WCS axis-aligned bounding-box clearance is only an axis separation check; it does not prove a lack of collisions or a functional door swing. Boundary probes are inconclusive: move them into the material or void. Unsupported checks must remain unresolved rather than being reported as passes.

Export native previews from known WCS directions. The preview tool restores view and selection; viewport pixel dimensions are not a scaled paper output. Inspect the PNG when appearance matters.

For `run_csharp` work, read [ACA API lessons](../../docs/aca-api-lessons.md). Materialize results inside the native transaction. Never return a lazy query over a side database that has already been disposed. Use synthetic fixtures from `tests/integration` to investigate an API uncertainty rather than experimenting in a user's project.

After a crash, distinguish saved state from the last successful in-memory operation. Reopen the last verified checkpoint at the appropriate xref base directory, inspect receipts and entity state, then resume only the missing stages. Do not replay an entire modeling script blindly.
