namespace Nodal.AcadMcp.Contracts;

/// <summary>
/// The working rules an agent driving the bridge should have in front of it. The MCP server sends
/// them as its server instructions, the bridge serves them at GET _guide, and the server's command
/// line prints them, so a session gets the same rules whichever way it reached AutoCAD. Each rule
/// is a mistake a real drawing session made. Keep it short: a client puts all of it in the model's
/// context on every session, and the details belong in the lessons documents it links to.
/// </summary>
public static class AgentGuide
{
    public const string Route = "_guide";

    public const string Text = """
        Nodal AcadMcp drives the AutoCAD session the user opened. Working rules, each one learned from real drawing work:

        Connecting
        - Use these tools. Without them, call the bridge with `Nodal.AcadMcp.Server.exe call <tool> @args.json` (run it with `help` for usage). Never drive AutoCAD over COM (GetActiveObject): one out-of-process call with a bad argument crashed AutoCAD and lost the session.

        While working
        - Read the drawing before changing it: acad_status, units, layers, what model space holds.
        - Save after every stage. A crash or hang loses everything since the last save, and paper-space edits can cause either. Keep every change, one-off settings included, in a stage file you can replay. A new drawing needs its real name saved before checkpoint_drawing works.
        - Paper space: use create_viewport. In run_csharp, never switch layouts and create, erase or turn on viewports in the same call.
        - Put back every system variable you change; set_system_variable returns the previous value.

        Reading the source
        - Take values from what is printed: dimension strings, angles, schedules, notes. When a PDF is vector, take lengths and angles from its geometry (read_pdf_page) instead of measuring a rendered image, and report anything you did measure as measured, with its tolerance. An angle measured off pixels once came out 0.05 degrees off the printed value; from the vectors it matched to 0.001.
        - Report every interpretation: a symbol read one way, a dimension derived from others, a view substituted for a missing one.

        Drafting conventions
        - Notes and callouts are multileaders (create_mleader). Never build an arrowhead out of lines.
        - Create annotative text, dimensions, leaders and blocks with the typed tools (create_text, create_mtext, create_dimension, create_mleader, insert_block); they size them correctly.
        - "Start the drawing N from the origin" means the building's reference corner, not the extents of the dimensions and notes. Say which point you used.
        - Roof pitch symbol (create_slope_symbol): a right triangle above the roof line, its hypotenuse parallel to the roof, the horizontal leg on top labelled 12 and the vertical leg dropping to the roof labelled with the rise.
        - Projection symbol (create_projection_symbol, ISO 128-3): a truncated cone's side view beside its end view, two circles. First angle: the cone's large end faces the circles. Third angle: its small end faces them.
        - Mark circle centres and axes with associative centre marks and centrelines (create_center_mark, create_centerline) rather than plain lines on a center layer.
        - Elevations: define plate, eave and ridge heights once and find every join by intersection, so the roof sits on the wall and lines meet exactly.

        Before saying it is done
        - Look at it: plot_preview for sheets, export_drawing_preview for model space. Run check_drafting for overlapping text, dimensions and leaders, doubled lines, slivers, and ends that nearly meet but do not.
        - AutoCAD Architecture: run validate_model with openings to catch casework and furniture in front of windows and doors. Do not add detail the source does not show: plain door and window styles unless the drawings show panels or a specific operation. Roof slabs meet at hips, valleys and ridges with plumb, mitered edges, and eave edges take the cut the design calls for: build the roof with create_roof and convert_roof_to_slabs rather than separate slabs.

        Lessons with the details: drafting-lessons.md and aca-api-lessons.md at https://github.com/ed-fos/autocad-mcp/blob/main/docs/
        """;
}
