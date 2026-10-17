"""Opt-in native ACA regression. Run only against a NEW, EMPTY, saved scratch DWG.

python workflow_regression.py --instance <per-session descriptor.json> --drawing <scratch.dwg>
Uses only Python's standard library. Never prints the session token or copies user drawings.
"""
import argparse
import json
import math
import pathlib
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--instance", required=True, type=pathlib.Path)
    parser.add_argument("--drawing", required=True, type=pathlib.Path)
    parser.add_argument("--report", required=True, type=pathlib.Path)
    parser.add_argument("--project", type=pathlib.Path, help="Optional disposable APJ copied from an Autodesk project template")
    options = parser.parse_args()
    instance = json.loads(options.instance.read_text(encoding="utf-8-sig"))
    drawing = str(options.drawing.resolve())
    report = []

    def call(name, args, expected_error=None):
        request = urllib.request.Request(f"http://127.0.0.1:{instance['port']}/{name}",
            data=json.dumps(args).encode(), headers={"Content-Type": "application/json", "X-Nodal-Token": instance["token"]})
        with urllib.request.urlopen(request, timeout=90) as response:
            envelope = json.load(response)
        # Keep the failing envelope too, so native failures remain diagnosable.
        report.append({"tool": name, "expectedError": expected_error, "result": envelope})
        options.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
        if expected_error:
            assert not envelope["ok"] and envelope["error"]["code"] == expected_error, envelope
        else:
            assert envelope["ok"], envelope
        return envelope.get("result")

    def cs(code, write=False):
        guard = f'if (!string.Equals(db.Filename, {json.dumps(drawing)}, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Wrong scratch drawing");\n'
        result = call("run_csharp", {"code": guard + code, "write": write})
        assert result.get("compiled", True), result
        return result["value"]

    def mutate(tool, key, **args):
        return call(tool, {"expectedDocument": drawing, "operationKey": key, **args})

    status = call("acad_status", {})
    assert status["active"]["isNamedDrawing"] and pathlib.Path(status["active"]["fileName"]).resolve() == options.drawing.resolve()
    assert status["acaAvailable"], "An ACA session is required."
    count = cs('var ms=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForRead); return ms.Cast<ObjectId>().Count();')
    assert count == 0, "Refusing to modify a nonempty drawing. Create a fresh scratch drawing."
    cs('db.Insunits=UnitsValue.Inches; return true;', True)

    # A long payload goes through the managed source callback, not the command-line buffer.
    result = call("run_lisp", {"expression": '(progn ' + '(+ 1 1)\n' * 2500 + '42)'})
    assert result["value"] == 42
    call("run_lisp", {"expression": '(progn ('}, expected_error="lisp_error")
    assert call("run_lisp", {"expression": '(+ 2 3)'})["value"] == 5

    fixture = cs('''
var ms=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForWrite);
var handles=new List<string>();
foreach(var dims in new[]{new[]{100.0,200,0,20,10,10},new[]{101.0,201,1,18,8,8}}){
 var body=Autodesk.Aec.Modeler.Body.Box(new Point3d(dims[0],dims[1],dims[2]),new Vector3d(dims[3],dims[4],dims[5]));
 var m=Autodesk.Aec.DatabaseServices.MassElement.Create(Autodesk.Aec.DatabaseServices.ShapeType.BoundaryRepresentation);
 m.SetToStandard(db);m.SetDatabaseDefaults(db);m.ChangeTo(Autodesk.Aec.DatabaseServices.ShapeType.BoundaryRepresentation);m.SetBody(body,false);
 if(m.StyleId.IsNull)throw new InvalidOperationException("Template needs a Standard mass style");
 ms.AppendEntity(m);tr.AddNewlyCreatedDBObject(m,true);handles.Add(m.Handle.ToString());
}
return handles;
''', True)
    outer, inner = fixture
    clone = mutate("clone_aec_style", "style", kind="mass", source="Standard", name="Regression mass")
    assert not clone["replayed"]
    assert mutate("clone_aec_style", "style", kind="mass", source="Standard", name="Regression mass")["replayed"]
    call("clone_aec_style", {"expectedDocument": drawing, "operationKey": "style", "kind": "mass", "source": "Standard", "name": "Changed"}, expected_error="operation_conflict")
    result = mutate("boolean_mass_elements", "carcass", target=outer, operands=[inner], operation="subtract", eraseOperands=True)
    assert math.isclose(result["result"]["volume"], 848, abs_tol=1e-5), result
    assert mutate("boolean_mass_elements", "carcass", target=outer, operands=[inner], operation="subtract", eraseOperands=True)["replayed"]
    validation = call("validate_model", {"expectedDocument": drawing, "entities": [{"handle": outer, "type": "MassElement", "minimumVolume": 847}],
        "probes": [{"handle": outer, "point": [110, 205, 5], "inside": False}, {"handle": outer, "point": [100.5, 205, 5], "inside": True}]})
    assert validation["passed"], validation

    profile = mutate("create_member_profile", "profile", name="Regression profile", profileUnits="Millimeters", drawingUnits="Inches",
        vertices=[[0,0,0],[25.4,0,0],[25.4,50.8,0],[0,50.8,0]])
    assert math.isclose(profile["result"]["area"], 2, abs_tol=1e-8)
    members = mutate("create_mitered_member_path", "miters", style="Regression profile", layer="0", points=[[0,0,30],[20,0,30],[30,10,30],[30,30,30]])["result"]["members"]
    assert len(members) == 3
    for first, second in zip(members, members[1:]):
        assert math.isclose(first["endSlope"], -second["startSlope"], abs_tol=1e-8)

    # Native objects reached through deferred queries must be materialized inside run_csharp's transaction.
    deferred = cs('var ms=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForRead); return ms.Cast<ObjectId>().Select(id=>tr.GetObject(id,OpenMode.ForRead));')
    assert outer in deferred and all(isinstance(item, str) for item in deferred)

    slab = call("create_slab", {"points": [[100,200,20],[140,200,20],[140,230,20],[100,230,20]], "thickness": 2, "layer": "0"})["handle"]
    hole = [[108,208,20],[112,208,20],[112,212,20],[108,212,20]]
    edited = mutate("edit_slab_openings", "hole", handle=slab, add=[hole])["result"]
    assert edited["loops"] == edited["beforeLoops"] + 1
    assert math.isclose(edited["beforeArea"] - edited["netArea"], 16, abs_tol=1e-6)
    removed = mutate("edit_slab_openings", "remove-hole", handle=slab, remove=[[110,210,20]])["result"]
    assert removed["loops"] == edited["beforeLoops"]
    mutate("edit_slab_openings", "restore-hole", handle=slab, add=[hole])

    # The imperial ACA template supplies a valid display representation template.
    solid = cs('var s=new Solid3d();s.CreateBox(20,10,10);s.TransformBy(Matrix3d.Displacement(new Point3d(300,200,0)-s.GeometricExtents.MinPoint));var ms=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForWrite);ms.AppendEntity(s);tr.AddNewlyCreatedDBObject(s,true);return s.Handle.ToString();', True)
    template = cs('var d=new Autodesk.Aec.DatabaseServices.DictionaryMultiViewBlockDefinition(db);return d.NamesInUse.Cast<string>().First();')
    mv = mutate("create_mvblock_from_solids", "mv-new", name="Regression MV", solids=[solid], templateDefinition=template, layer="0", basePoint=[300,200,0])["result"]["handle"]
    cs(f'var m=(Autodesk.Aec.DatabaseServices.MultiViewBlockReference)tr.GetObject(db.GetObjectId(false,new Handle(Convert.ToInt64("{mv}",16)),0),OpenMode.ForWrite);m.Scale=new Scale3d(2,3,4);m.Rotation=Math.PI/4;return true;', True)
    replacement = mutate("create_mvblock_from_solids", "mv-replace", name="Regression MV replacement", solids=[solid], templateDefinition=template, layer="0", replaceHandle=mv)["result"]
    assert replacement["scale"] == [2,3,4] and math.isclose(replacement["rotationRadians"], math.pi/4)
    round_trip = cs(f'''
var m=(Autodesk.Aec.DatabaseServices.MultiViewBlockReference)tr.GetObject(db.GetObjectId(false,new Handle(Convert.ToInt64("{mv}",16)),0),OpenMode.ForRead);
var def=(Autodesk.Aec.DatabaseServices.MultiViewBlockDefinition)tr.GetObject(m.BlockDefId,OpenMode.ForRead);
var b=(BlockTableRecord)tr.GetObject(def.InterferenceBlockId,OpenMode.ForRead);
using var s=(Solid3d)((Solid3d)tr.GetObject(b.Cast<ObjectId>().Single(),OpenMode.ForRead)).Clone();
var transform=Matrix3d.Displacement(m.Location.GetAsVector())*Matrix3d.Rotation(m.Rotation,Vector3d.ZAxis,Point3d.Origin)*new Matrix3d(new[]{{m.Scale.X,0,0,0,0,m.Scale.Y,0,0,0,0,m.Scale.Z,0,0,0,0,1.0}});
s.TransformBy(transform);var e=s.GeometricExtents;return new[]{{e.MinPoint.X,e.MinPoint.Y,e.MinPoint.Z,e.MaxPoint.X,e.MaxPoint.Y,e.MaxPoint.Z,s.MassProperties.Volume}};
''')
    assert all(math.isclose(a,b,abs_tol=1e-5) for a,b in zip(round_trip,[300,200,0,320,210,10,2000])), round_trip

    checkpoint = options.drawing.with_name(options.drawing.stem + "-checkpoint.dwg")
    preview = options.drawing.with_suffix(".png")
    saved = call("checkpoint_drawing", {"expectedDocument": drawing, "path": str(checkpoint), "expectedHandles": [outer, slab]})
    assert saved["verified"] and len(saved["sha256"]) == 64 and saved["bytes"] > 1000
    view_before = cs('using var v=ed.GetCurrentView();return new[]{v.Width,v.Height,v.Target.X,v.Target.Y,v.Target.Z,v.ViewDirection.X,v.ViewDirection.Y,v.ViewDirection.Z};')
    exported = call("export_drawing_preview", {"expectedDocument": drawing, "path": str(preview), "handles": [outer, slab], "direction": [1,-1,1]})
    assert exported["bytes"] > 100 and preview.read_bytes()[:8] == b'\x89PNG\r\n\x1a\n'
    view_after = cs('using var v=ed.GetCurrentView();return new[]{v.Width,v.Height,v.Target.X,v.Target.Y,v.Target.Z,v.ViewDirection.X,v.ViewDirection.Y,v.ViewDirection.Z};')
    assert all(math.isclose(a,b,rel_tol=1e-8,abs_tol=1e-8) for a,b in zip(view_before,view_after)), (view_before,view_after)
    call("checkpoint_drawing", {"expectedDocument": drawing, "path": str(checkpoint)}, expected_error="already_exists")
    call("edit_slab_openings", {"expectedDocument": drawing + "-wrong", "operationKey": "wrong-doc", "handle": slab, "remove": [[110,210,20]]}, expected_error="wrong_document")
    call("create_mitered_member_path", {"expectedDocument": drawing, "operationKey": "bad-point", "style": "Regression profile", "layer": "0", "points": [[0,0],[1,1,1]]}, expected_error="bad_argument")

    if options.project:
        project = str(options.project.resolve())
        project_source = cs(f'''
using var pointer=new Autodesk.Aec.Project.ProjectPointer({json.dumps(project)},OpenMode.ForWrite,false);pointer.SaveOnClose=true;
var p=pointer.Object;if(p.GetConstructs().Any(f=>f.FileName=="RegressionSource"))throw new InvalidOperationException("Use a fresh test project");
var info=new Autodesk.Aec.Project.ProjectFile(p){{FileName="RegressionSource",Category="Constructs",FileCategory=Autodesk.Aec.Project.FileCategory.System,Description="Synthetic regression source"}};
if(!p.AddConstruct(info,db.Filename,Autodesk.Aec.Project.FileAddOption.Copy))throw new InvalidOperationException("Could not create source construct");
return System.IO.Path.GetFullPath(info.DrawingFullPath);
''')
        cs(f'Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.Open({json.dumps(project_source)},false);return true;')
        copied = call("create_project_construct", {"expectedDocument": project_source, "expectedProject": project, "name": "RegressionOption"})
        assert copied["modelSpaceEntities"] == saved["modelSpaceEntities"]
        assert pathlib.Path(copied["path"]).is_file()
        call("create_project_construct", {"expectedDocument": project_source, "expectedProject": project, "name": "RegressionOption"}, expected_error="already_exists")
    print(f"Passed {len(report)} native calls. Artifacts and report: {options.report}")


if __name__ == "__main__":
    main()
