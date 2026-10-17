# 2D drafting and sheet production lessons

These notes come from one session that drew a small two-sheet architectural set in AutoCAD 2027. The set had plans, a reflected ceiling plan, foundation and roof plans, and four elevations on Arch D sheets from the ACA sheet template. Almost every step went through `run_csharp`, plus a little `run_lisp` and `set_system_variable`. They are the 2D counterpart of the [ACA API lessons](aca-api-lessons.md): implementation notes measured in that drawing, not a general theory of AutoCAD. Imperial units throughout. The drawing scale was 1/4" = 1'-0", so the scale factor is 48 and 3/32" paper text is 4.5 model units.

## run_csharp mechanics worth knowing first

- `doc`, `db`, `tr` and `ed` are the snippet's parameters. `var doc = ...` is CS0136.
- `write:false` aborts the transaction and takes no document lock. Appending an entity there fails with `eLockViolation`.
- Iterate a `DBDictionary` with `foreach (DBDictionaryEntry e in dict)`. `dict.Cast<DBDictionaryEntry>()` throws `InvalidCastException`, because the non-generic enumerator yields `System.Collections.DictionaryEntry`.
- `PlottingServices` is imported by default, and `PlotType` means `Autodesk.AutoCAD.DatabaseServices.PlotType`. With ACA loaded, `ContentType` means the AutoCAD (MLeader) one rather than ACA's.
- `StandardScaleType` has no member for 1/4" = 1'-0" under any guessed name (CS0117). Set `Viewport.CustomScale = 1.0 / 48` instead.
- Put a helper library in `prelude` rather than pasting it in front of every snippet. Errors then number prelude lines and code lines separately. A runtime error names the library call that threw and the snippet line that reached it.

## Annotative objects: order of operations

Set up annotative styles first: a text style with `Annotative = True` and `SetPaperOrientation(true)`, a dimension style with `Annotative = True`, `Dimscale = 1` and paper-size `Dimtxt`/`Dimasz`, and an annotative multileader style. Then set `CANNOSCALE` (`db.Cannoscale`) to the drawing scale. Objects created through the API do **not** pick any of this up the way commands do:

- **Flag before appending.** Set `Annotative = AnnotativeStates.True` before `AppendEntity` on DBText, MText, dimensions and multileaders. For a `RotatedDimension` the order is `SetDatabaseDefaults(db)`, `DimensionStyle`, points, `Annotative`, then append. Its text then came out 4.5 high, matching DIMLINEAR. Flagged after append, the dimension text was 3/32 ÷ 48 = 0.00195 high. Overriding `Dimscale`/`Dimtxt` on such a dimension then throws `eInvalidInput`.
- **Text height is not scaled for you.** DBText and MText keep whatever height you set, annotative or not. Set paper height × scale (3/32 × 48 = 4.5), as the TEXT command stores it. The working helper set `TextHeight` (and MText `Width`) before the append and again after it. For a multileader, set the MText content's `TextHeight` to the model height too.
- **Annotative block references do not work through the API.** `BlockReference.Annotative = True` throws `eInvalidInput`. An annotative block definition with an annotative attribute, given the scale context after the append, scaled its geometry 48× while the attribute stayed paper size. What worked was paper-size, non-annotative symbol blocks inserted with `Scale3d(48)`.
- **Scales are found by value, not name.** Scale names come from the drawing's scale list, e.g. `1/4" = 1'-0"`, so `GetContext("1:1")` returned null. Walk `db.ObjectContextManager.GetContextCollection("ACDB_ANNOTATIONSCALES")` and compare `DrawingUnits / PaperUnits`.
- **CANNOSCALE is per space.** With a layout current it read `1'-0" = 1'-0"`, while model space still held `1/4" = 1'-0"`. Make Model current before reading or setting the model annotation scale. Give each viewport its own `AnnotationScale` explicitly.

## Multileaders

- **Vertex order:** `AddLeaderLine(point nearest the landing)`, then `AddFirstVertex(leaderIndex, arrowTip)`. `AddLeaderLine(tip)` followed by `AddFirstVertex(bend)` misplaced the arrowheads at bends. `AddLastVertex(bend)` produced runaway vertices whose huge extents also broke extents-based cleanup, so model space had to be wiped and regenerated.
- `TextAngleType = TextAngleType.HorizontalAngle` keeps the text level on a sloped leader. Set `SetDogleg` and `TextAlignmentType` to match the side the text is on.

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
- **PDFIMPORTIMAGEPATH defaults to the relative `PDF Images`,** which resolves against the PDF's folder. Importing wrote a new `PDF Images` folder full of PNGs into the folder holding the user's source PDF. Point it at a temporary folder before importing, and restore it afterwards.
- A `setvar` inside a `progn` that later fails still sticks. The PDFIMPORTIMAGEPATH change survived a failed expression, and the AutoCAD restart after a crash. Treat every sysvar write as done the moment it runs. `PDFIMPORTFILTER 15` was rejected; 7 was accepted.

## System variables

- Change a variable through `set_system_variable`, which returns its `previous` value. Keep those values and set them back at the end. Variables changed inside `run_csharp` or `run_lisp` have no recorded previous value: one (BACKGROUNDPLOT) had to be restored from a guess.
- Profile variables (PDFIMPORT*, BACKGROUNDPLOT, FILEDIA, …) outlive the drawing, the session and a crash, so a missed restore lingers.

## Crash recovery

The drawing had last been saved at template state when AutoCAD crashed during a layout script, so about 50 minutes of in-memory work were lost. Every stage had been a script file, so the rebuild took about 15 seconds. The exception was a one-off `DIMTFILL` change made in an ad-hoc call, which was missed and noticed later.

- **Save after every stage**, not at the end. For a new drawing, save it under its real name first: `checkpoint_drawing` needs a named drawing.
- **Keep every mutation in a replayable file**, one-off settings included. A sysvar, a style tweak or a purge belongs in a stage script, not only in the chat history.
- **Replay only into a verified starting state.** A full replay into a drawing confirmed to be at template state is fine: empty model space, the template's layer count, no stage blocks. Replaying into a drawing that already holds some stages duplicates them. A rebuild script with a `-From` stage parameter, saving between stages, made both cases easy.
- AutoCAD's autosave (`%TEMP%\*.sv$`) may hold newer state than the DWG, but it is a fallback to inspect, not a plan.
