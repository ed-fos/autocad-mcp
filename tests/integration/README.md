# Native workflow regression

The pure test project runs without AutoCAD. These tests require ACA 2027 with the candidate add-in loaded and the bridge explicitly started. Run against a disposable session and a **new, empty, saved scratch DWG**, never an existing project drawing.

```powershell
python tests/integration/workflow_regression.py `
  --instance "$env:LOCALAPPDATA\Nodal.AcadMcp\instances\<acad-pid>.json" `
  --drawing "C:\CAD\Test\Scratch.dwg" `
  --report "C:\CAD\Test\workflow-report.json"
```

Use a fresh scratch DWG per run. The runner refuses a nonempty drawing and creates named test styles, mass solids, structural members, a slab, a checkpoint and a PNG. It does not delete or clean up user files. Its report stays at the supplied path and contains no bridge token. Keep native DWGs, images and machine-specific reports out of Git.

Use the installed **Aec Model (Imperial Ctb)** template; it supplies native Standard styles and an MV display representation template. The suite covers long/erroring LISP, deferred native queries, style cloning, native boolean volume/void checks, operation replay/conflicts, profile units, miter plane slopes, slab holes and area, MV creation and replacement with nonuniform scale, checkpoint readback, PNG export and view restoration, schema rejection and wrong-document rejection.

For project-copy coverage, copy an Autodesk template project to a disposable directory and add `--project <copied-template.apj>`. The runner creates `RegressionSource` and `RegressionOption` constructs there and verifies native registration and drawing counts. It does not switch Project Navigator's current project. Use a fresh project per run; existing fixture names are rejected.
