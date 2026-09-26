using System.Diagnostics;

namespace Nodal.AcadMcp.Addin;

/// <summary>
/// Runtime detection of AutoCAD Architecture.
///
/// Deliberately references NO Aec type. The JIT resolves a method's type references when that
/// method is first called, so ACA-typed code must live in methods that are never invoked on a
/// vanilla AutoCAD box. This probe is the gate that guarantees that.
/// </summary>
internal static class AcaSupport
{
    private static bool? _available;

    internal static bool IsAvailable => _available ??= Probe();

    private static bool Probe()
    {
        try
        {
            // AecBase.dbx is loaded as a native module whenever the ACA object enabler is live.
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                var name = module.ModuleName;
                if (name is not null && name.StartsWith("AecBase", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Enumerating modules can fail under tight security policy; treat as "no ACA".
        }
        return false;
    }

    internal static void Require()
    {
        if (!IsAvailable)
            throw new AcadMcpException("aca_unavailable",
                "AutoCAD Architecture is not loaded in this session.",
                "Start AutoCAD with the ACA profile (/product ACA) to use AEC tools.");
    }

    internal static AcadMcpException Unavailable() => new("aca_unavailable", "This build does not include AutoCAD Architecture support.");
}
