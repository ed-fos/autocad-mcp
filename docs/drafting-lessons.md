# 2D drafting and sheet production lessons

These notes come from one session that drew a small two-sheet architectural set in AutoCAD 2027. The set had plans, a reflected ceiling plan, foundation and roof plans, and four elevations on Arch D sheets from the ACA sheet template. Almost every step went through `run_csharp`, plus a little `run_lisp` and `set_system_variable`. They are the 2D counterpart of the [ACA API lessons](aca-api-lessons.md): implementation notes measured in that drawing, not a general theory of AutoCAD. Imperial units throughout. The drawing scale was 1/4" = 1'-0", so the scale factor is 48 and 3/32" paper text is 4.5 model units.

The last two sections, [Reading the source drawing](#reading-the-source-drawing) and [Drafting conventions](#drafting-conventions), cover mistakes found on review of drawings like that set and of mechanical exercises (orthographic views of an isometric part, and a 3D solid from orthographic views). The short version of all of it is in the bridge's working rules (`Nodal.AcadMcp.Server.exe guide`), which an MCP client receives as the server's instructions.

## run_csharp mechanics worth knowing first

- `doc`, `db`, `tr` and `ed` are the snippet's parameters. `var doc = ...` is CS0136.
- `write:false` aborts the transaction and takes no document lock. Appending an entity there fails with `eLockViolation`.
- Iterate a `DBDictionary` with `foreach (DBDictionaryEntry e in dict)`. `dict.Cast<DBDictionaryEntry>()` throws `InvalidCastException` as soon as it is enumerated, because the non-generic enumerator yields `System.Collections.DictionaryEntry`. A bare `.Count()` on it still works, because it reads the collection's count without casting, so a quick probe can look fine.
- `PlottingServices` is imported by default, and `PlotType` means `Autodesk.AutoCAD.DatabaseServices.PlotType`. With ACA loaded, `ContentType` means the AutoCAD (MLeader) one rather than ACA's.
- `StandardScaleType` has no member for 1/4" = 1'-0" under any guessed name (CS0117). Set `Viewport.CustomScale = 1.0 / 48` instead.
- Put a helper library in `prelude` rather than pasting it in front of every snippet. Errors then number prelude lines and code lines separately. A runtime error names the library call that threw and the snippet line that reached it.

## Annotative objects: order of operations

Set up annotative styles first: a text style with `Annotative = True` and `SetPaperOrientation(true)`, a dimension style with `Annotative = True`, `Dimscale = 1` and paper-size `Dimtxt`/`Dimasz`, and an annotative multileader style. Then set `db.Cannoscale` to the drawing scale. Objects created through the API do **not** pick up the style's annotative flag the way commands do. Set `Annotative = AnnotativeStates.True` on each entity. A later set of controlled measurements, made the same day for the annotative typed tools, agrees with the drawing session except where noted:

- **Dimensions: flag before appending.** The order is `SetDatabaseDefaults(db)`, `DimensionStyle`, points, `Annotative`, then append. The text then came out 4.5 high, matching DIMLINEAR. Flagged after append, it was 3/32 ÷ 48 = 0.00195 high. Overriding `Dimscale`/`Dimtxt` on such a dimension then throws `eInvalidInput`. DBText and MText can be flagged before or after the append; before is the safe habit.
- **Text height is not scaled for you.** DBText and MText keep whatever height you set, annotative or not. Set paper height × scale (3/32 × 48 = 4.5), as the TEXT command stores it. `SetDatabaseDefaults` gives TEXTSIZE, not a fixed-height style's height. Settle the scale contexts first: adding or removing a context rescales the object proportionally. The working helper set `TextHeight` (and MText `Width`) before the append and again after it.
- **Multileaders take paper units.** An annotative `MLeader.TextHeight` is the plotted height: 3/32 gives 4.5 in model space, and 4.5 gives 216. Set it after assigning `ml.MText`, because the new MText carries TEXTSIZE, which overrides the style's height. The drawing session's helper set the content MText's own `TextHeight` to 4.5 before assigning it, and that plotted at 3/32.
- **Annotative blocks need the context before their attributes.** `BlockReference.Annotative = True` throws `eInvalidInput`. A reference to an annotative block definition gets no scale context when appended. Call `br.AddContext(scale)` before creating the attribute references: `SetAttributeFromBlock` then uses the scaled `BlockTransform`, and the attributes come out at model size. Added after them, the attributes stay paper size. The drawing session hit the paper-size attributes and used paper-size, non-annotative symbol blocks inserted with `Scale3d(48)` instead, which also works.
- **Scales are found by value, not name.** Scale names come from the drawing's scale list, e.g. `1/4" = 1'-0"`, so `GetContext("1:1")` returned null. Walk `db.ObjectContextManager.GetContextCollection("ACDB_ANNOTATIONSCALES")` and compare `DrawingUnits / PaperUnits`.
- **The CANNOSCALE variable is per space.** With a layout current, the system variable read `1'-0" = 1'-0"`, while `db.Cannoscale` still returned model space's `1/4" = 1'-0"`. Use `db.Cannoscale` for the model scale. Give each viewport its own `AnnotationScale` explicitly. With ANNOAUTOSCALE on, changing `db.Cannoscale` also adds the new scale to existing annotative objects.

## Multileaders

- **Vertex order:** `AddLeaderLine(point nearest the landing)`, then `AddFirstVertex(leaderIndex, arrowTip)`. For more bends, prepend each one from the landing outward, ending with the tip: `AddFirstVertex(bend)`, then `AddFirstVertex(tip)`, gives [tip, bend, near, landing]. `AddLeaderLine(tip)` followed by `AddFirstVertex(bend)` misplaced the arrowheads at bends. `AddLastVertex(bend)` produced runaway vertices whose huge extents also broke extents-based cleanup, so model space had to be wiped and regenerated.
- `TextAngleType = TextAngleType.HorizontalAngle` keeps the text level on a sloped leader. Set `SetDogleg` and `TextAlignmentType` to match the side the text is on.
- Place the text with `MText.Location`. Setting `MLeader.TextLocation` after `AddLeaderLine` nudged the text off the point.

## Hatches

`Hatch.PatternAngle` (and `PatternScale` / `PatternSpace`) throws `eInvalidInput` on a hatch that has no pattern yet. The order that worked:

1. `new Hatch()`, `SetDatabaseDefaults(db)`, append.
2. `SetHatchPattern(type, name)`.
3. `PatternAngle`, then `PatternScale` (or `PatternSpace` for `_USER`).
4. `SetHatchPattern(type, name)` again. The working helper repeated this call so the pattern is regenerated with the new angle and scale; whether it is strictly needed was not isolated.
5. `Associative = false`, `Origin`, `AppendLoop` with the outermost loop first, then `EvaluateHatch(true)`.

## Text and attributes

- **Justified text:** for anything other than BaseLeft, set the justification (`HorizontalMode`/`VerticalMode`, or `Justify` on attributes) and `AlignmentPoint`, append, then call `AdjustAlignment(db)`. The same goes for `AttributeDefinition` and `AttributeReference`.
- **Attributes on rotated blocks:** `SetAttributeFromBlock(ad, br.BlockTransform)` rotates the attribute with the block. To keep a tag upright, set `Rotation = 0` and place the attribute at the block position plus the definition's *unrotated* offset × scale. Transforming the definition point by `BlockTransform` stacked the tags of rotated markers on top of each other.
- **Text over hatching:** use MText with `BackgroundFill`, `UseBackgroundColor` and `BackgroundScaleFactor` (1.15 worked). For dimensions, set `DIMTFILL = 1` on the style, then `RecomputeDimensionBlock(true)` on existing dimensions.

## Paper-space viewports

AutoCAD 2027 has two viewport failures that lose all unsaved work. Both were reproduced through `run_csharp`:

- **Hang.** Making a layout current in the same call that erased viewports already committed on that layout spins AutoCAD forever. A re-run of a layout script that erases its own earlier viewports is exactly this case.
- **Crash.** Setting `Viewport.On = true` on a viewport created in the same call crashes AutoCAD intermittently, roughly 1 run in 4, with an access violation. Of 31 crash dumps, 27 died when the call committed and 4 at a later `LayoutManager.CurrentLayout` switch in the same call. The bridge's commit path was ruled out as the cause.

The safe pattern, 10/10 in testing:

1. Switch layouts in a call of their own.
2. Create or erase viewports in the next call, without switching.
3. Turn the new viewports On in a later call, with their layout current.

Plotting switches layouts too, so plot in a call that does not create or erase viewports.

The viewport recipe that worked:

1. `new Viewport()`, `SetDatabaseDefaults(db)`.
2. `CenterPoint`, `Width` and `Height` in paper units (view size ÷ 48).
3. A no-plot layer, then append.
4. `ViewDirection = Vector3d.ZAxis`, `ViewTarget`, `ViewCenter`, `TwistAngle = 0`.
5. `CustomScale = 1.0 / 48`, `AnnotationScale`, `Locked = true`.
6. Turn it On in a later call, as above.

Named model views make good viewport sources. Setting `IsPaperspaceView` or `Annotative` on a `ViewTableRecord` threw `eNotImplementedYet`, so leave both at their defaults.

## Title-block fields

The ACA sheet template's title block uses Sheet Set Manager fields (`%<\AcSm Sheet.Number>%`, `Sheet.Drawn By`, …) and ACA project fields (`%<\AecPr "ProjectName">%`, …). In a drawing with no sheet set and no ACA project, they display `####`. What worked:

1. Write drawing properties through `var b = new DatabaseSummaryInfoBuilder(db.SummaryInfo)`. Set `b.Title` etc. and the custom properties in `b.CustomPropertyTable` (`Contains`, then index or `Add`), then `db.SummaryInfo = b.ToDatabaseSummaryInfo()`.
2. Replace each MText's contents with a `%<\AcVar CustomDP.Name>%` field: `m.Contents = ""; var f = new Field(codes) { EvaluationOption = FieldEvaluationOptions.Automatic }; m.SetField(f); tr.AddNewlyCreatedDBObject(f, true); f.Evaluate();`. One field code string can mix several fields with MText formatting.
3. Set the per-sheet values (sheet number, title, index, total) as plain text. Without a sheet set there is nothing for a field to read.

Find the template's MText by its location, and when re-running, clear only what you added, by layer. A cleanup rule keyed on text height erased the template's own filename text.

## Plotting to PDF

- Set `BACKGROUNDPLOT` to 0 for the duration (read the old value first and restore it). Check `PlotFactory.ProcessPlotState == ProcessPlotState.NotPlotting`.
- **Check the template's page setup before trusting it.** The Arch D layout was set up for DWF6 ePlot on portrait `ARCH_expand_D_(24.00_x_36.00_Inches)` media, rotated 90°, with a plot origin of (6.88, 27.27). The setup that worked, through `PlotSettingsValidator.Current` on the Layout:
  - `SetPlotConfigurationName(lo, "DWG To PDF.pc3", "ARCH_full_bleed_D_(36.00_x_24.00_Inches)")`, `RefreshLists`, `SetPlotPaperUnits(Inches)`.
  - `SetPlotRotation(Degrees000)`, `SetPlotWindowArea` (the layout limits), `SetPlotType(PlotType.Window)`, `SetPlotCentered(false)`, `SetPlotOrigin(0,0)`.
  - `SetUseStandardScale(true)`, `SetStdScaleType(StdScale1To1)`, `SetCurrentStyleSheet("monochrome.ctb")`.
  - `PrintLineweights = true`, `PlotPlotStyles = true`, `ScaleLineweights = false`.
- **The layout must be current to plot it.** Otherwise the plot fails with `eLayoutNotCurrent`. Make it current, then:
  1. `new PlotInfo { Layout = layout.ObjectId }`.
  2. `new PlotSettings(false)`, `CopyFrom(layout)`, assigned as `OverrideSettings`.
  3. `new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(pi)`.
- **One PDF per sheet:** `PlotFactory.CreatePublishEngine()`, then `BeginPlot`, `BeginDocument(pi, doc.Name, null, 1, true, pdfPath)`, `BeginPage(new PlotPageInfo(), pi, true, null)`, `BeginGenerateGraphics`/`EndGenerateGraphics`, `EndPage`, `EndDocument`, `EndPlot`.
- **A combined set:** one engine and one `BeginDocument`, then `BeginPage(..., isLastPage: i == count - 1, ...)` for each layout in tab order.
- **Visual QA:** plot a window to PDF, render the PDF to PNG, and look. Model space works with `PlotType.Window`, `ScaleToFit` and centring. The session rendered with WinRT `Windows.Data.Pdf` from PowerShell, with no extra tools, cropping to the region of interest. It caught runaway leaders, label collisions and mirrored text order that the numbers did not show.

## Importing a PDF

- `(command "_.-PDFIMPORT" "_File" path page insertionPoint scale rotation)` through `run_lisp` works with FILEDIA set to 0. `send_command` with `mode:'command'` does not (see its description).
- **PDFIMPORTIMAGEPATH defaults to the relative `PDF Images`,** which resolves against the PDF's folder. Importing wrote a new `PDF Images` folder full of PNGs into the folder holding the source PDF. Point it at a temporary folder before importing, and restore it afterwards.
- A `setvar` inside a `progn` that later fails still sticks. The PDFIMPORTIMAGEPATH change survived a failed expression, and the AutoCAD restart after a crash. Treat every sysvar write as done the moment it runs.
- **PDFIMPORTFILTER lists what to leave out, not what to bring in.** 8, the default, leaves out raster images; 0 brings everything; 15, which would leave out everything, is rejected. Setting it to 7 to "include vectors, fills and text" imports the raster images only, which looks exactly like a scanned page.
- **SHX lettering imports as linework.** A PDF made by AutoCAD with SHX fonts has no text objects to import, only thousands of short polylines. PDFSHXTEXT recognition was slow (over 15 s for one sheet) and partial (40 labels, mostly symbols), so it is no route to the printed numbers.
- `read_pdf_page` does the import safely: into model space between its own UNDO marks, with the image path redirected and every variable restored, reading what arrived and then undoing it. On a vector site plan its line angles matched the printed bearing to 0.001°.

## System variables

- Change a variable through `set_system_variable`, which returns its `previous` value. Keep those values and set them back at the end. Variables changed inside `run_csharp` or `run_lisp` have no recorded previous value: one (BACKGROUNDPLOT) had to be restored from a guess.
- Profile variables (PDFIMPORT*, BACKGROUNDPLOT, FILEDIA, …) outlive the drawing, the session and a crash, so a missed restore lingers.

## Crash recovery

The drawing had last been saved at template state when AutoCAD crashed during a layout script, so about 50 minutes of in-memory work were lost. Every stage had been a script file, so the rebuild took about 15 seconds. The exception was a one-off `DIMTFILL` change made in an ad-hoc call, which was missed and noticed later.

- **Save after every stage**, not at the end. For a new drawing, save it under its real name first: `checkpoint_drawing` needs a named drawing.
- **Keep every mutation in a replayable file**, one-off settings included. A sysvar, a style tweak or a purge belongs in a stage script, not only in the chat history.
- **Replay only into a verified starting state.** A full replay into a drawing confirmed to be at template state is fine: empty model space, the template's layer count, no stage blocks. Replaying into a drawing that already holds some stages duplicates them. A rebuild script with a `-From` stage parameter, saving between stages, made both cases easy.
- AutoCAD's autosave (`%TEMP%\*.sv$`) may hold newer state than the DWG, but it is a fallback to inspect, not a plan.
- **Never reach AutoCAD over COM.** Driving AutoCAD through `GetActiveObject("AutoCAD.Application")` from another process bypasses the main-thread marshalling and undo grouping, and one `AddRegion` call with a bad argument crashed AutoCAD outright (HRESULT 0x800706BE, "The remote procedure call failed"). Use the tools, `Nodal.AcadMcp.Server.exe call`, or raw HTTP.

## Reading the source drawing

- **Printed values beat measured ones.** Measuring a rendered PDF page in pixels works for lengths that are also printed, but an angle measured that way came out 0.05° off the value printed on the sheet, which is a visible error over a building's length. Read dimension strings, angles, schedules and notes first, measure only what the sheet does not state, and report measured values as measured, with a tolerance.
- **Find out whether a PDF is vector before measuring it.** `read_pdf_page` (see [Importing a PDF](#importing-a-pdf)) brings a vector page's lines and any real text back as coordinates, so lengths and angles come out exact; `minLength` drops the short strokes of SHX lettering and `window` narrows it to one view. A page can also come in as raster images only, either because it is a scan or because PDFIMPORTFILTER left vectors out. Only then fall back to a rendered image.
- **Report every interpretation.** A symbol read one way, a dimension derived from others, a view substituted for a missing one, a hole position the source did not give. Listing them saves the reviewer a search through the drawing.

## Drafting conventions

Each of these has been drawn wrong by an agent that could state the rule.

- **Notes are multileaders.** A helper that draws an arrowhead out of two short lines produces "leaders" that are not leaders, and mixed with real MLeaders the set is inconsistent. Use `create_mleader` (bent leaders through `vertices`, level text through `textAngle`).
- **"Start the drawing N from the origin" means the building.** Moving everything so the extents of all entities, dimensions and notes included, begin at the offset puts the building somewhere arbitrary. Put the building's reference corner (the outside corner of the walls, or grid A-1) at the offset, and say which point that was.
- **Roof pitch symbol.** A right triangle drawn above the roof line with its hypotenuse parallel to the roof: the horizontal leg on top, labelled 12, and the vertical leg dropping from its end to the roof line, labelled with the rise. Drawn with the horizontal leg at the bottom, it hangs below the roof line, flipped. `create_slope_symbol` draws it from the roof line itself and reads the rise off the line's slope.
- **Projection symbol.** ISO 128-3 shows a truncated cone's side view (a trapezoid) beside its end view (two concentric circles). In first angle the cone's large end faces the circles; in third angle its small end does. A trapezoid tapering toward the circles is the third-angle symbol, whatever the label next to it says. `create_projection_symbol` draws either; check against the figure in ISO 128-3 section 4.7 when in doubt.
- **Centre marks and centrelines are objects.** AutoCAD's CENTERMARK and CENTERLINE commands make associative objects that follow the circle or the pair of lines they mark and extend past the edge, which is what a drafter expects to find; plain lines on a CENTER layer do neither. `create_center_mark` and `create_centerline` run the commands; both leave a block reference, and both commands want the object and a point on it, `(command "_.CENTERMARK" (list ename point) "")`.
- **Elevations come from shared datums.** Computing each elevation line from its own constant leaves nothing tying the roof's underside to the wall plate: the result can have a roof sitting above the wall, doubled lines, and joins that are not cleaned up. Define plate, heel, eave and ridge heights once, derive every line from them, and find each join by intersecting the two lines rather than by typing a number again.
- **Dimensions and notes must not collide.** Space dimension chains at a fixed distance outward from the building, keep notes off them, and look at the plotted sheet (`plot_preview`) before calling it done. A visual pass catches some collisions and misses others; `check_drafting` reports overlapping annotation, doubled lines, slivers and ends that stop short of what they should meet, with coordinates.
