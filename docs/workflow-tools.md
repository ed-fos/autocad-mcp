# Recoverable ACA workflows

The workflow tools expose bounded JSON schemas through MCP. Existing tools retain their previous permissive discovery schema. The add-in validates new schemas before invoking native code.

| Tool | Purpose | Undo |
| --- | --- | --- |
| `checkpoint_drawing` | Save, copy, reopen and hash a new DWG checkpoint | Files cannot be undone |
| `export_drawing_preview` | Frame chosen entities, export PNG, restore view/settings | Export cannot be undone |
| `validate_model` | Entity expectations, native volume, WCS point probes, axis clearances | Read only |
| `create_project_construct` | Copy the saved construct within its current project category | Project/files cannot be undone |
| `clone_aec_style` | Clone a mass/slab/member style or MV definition | Drawing transaction |
| `boolean_mass_elements` | Union/subtract/intersect copied native mass bodies | Drawing transaction |
| `create_member_profile` | Closed contour to shape and member style, explicit units | Drawing transaction |
| `create_mitered_member_path` | Native braces with shared miter planes on an open horizontal path | Drawing transaction |
| `create_mvblock_from_solids` | New model block/MV definition from WCS solids | Drawing transaction |
| `edit_slab_openings` | Add/remove native slab openings | Drawing transaction |

All tools require `expectedDocument`, the full path of the active saved DWG. The six transactional authoring tools additionally require an `operationKey`. Use a fresh key for each intended edit and the same key for an identical retry. A key is not a request to overwrite an existing style, definition or file.

Example profile in a drawing whose INSUNITS is Inches:

```json
{
  "expectedDocument": "C:\\CAD\\Scratch.dwg",
  "operationKey": "trim-profile-v1",
  "name": "Trim 1x2",
  "profileUnits": "Millimeters",
  "drawingUnits": "Inches",
  "vertices": [[0,0,0],[25.4,0,0],[25.4,50.8,0],[0,50.8,0]]
}
```

Each vertex is `[profileX, profileY, bulgeToNext]`; it is not a 3D WCS point. Omit the repeated closing vertex. `create_mitered_member_path` takes WCS `[x,y,z]` points, a style name and an existing unlocked layer.

Transactional results have `{ "replayed": false, "result": ... }`, or `true` for a reused receipt. Save checkpoints between stages to persist geometry and receipts. Disk writes use new destinations and do not implicitly overwrite anything.

Point probes support mass elements and slabs. Boundary and unsupported probes fail explicitly. Axis clearances use WCS bounding boxes and report negative overlap on the chosen axis; these checks cannot certify exact fit. MV creation needs an existing template definition with valid display representations. Project construct creation currently copies within the same category only. Native PNG output uses the current viewport's pixel resolution.

For API details, see [lessons](aca-api-lessons.md). The reusable agent workflow is [skills/aca-modeling](../skills/aca-modeling/SKILL.md). Native regression instructions are in [tests/integration](../tests/integration/README.md).

See the [validation status](workflow-validation.md) for passed checks and remaining native release checks. A checkpoint restored to its original full DWG path can reuse the same operation arguments. If opened under a different name, the changed `expectedDocument` also changes the receipt fingerprint; inspect the existing geometry before resolving that conflict.
