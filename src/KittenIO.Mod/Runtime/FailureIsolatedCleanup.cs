using KittenIO.Mod.Diagnostics;

namespace KittenIO.Mod.Runtime;

internal static class FailureIsolatedCleanup
{
    public static void Run(BridgeDiagnostics? diagnostics, params CleanupStep[] steps)
    {
        foreach (var step in steps)
        {
            try
            {
                step.Action();
            }
            catch (Exception exception)
            {
                diagnostics?.ReportError($"{step.Name} failed", exception);
            }
        }
    }
}

internal readonly record struct CleanupStep(string Name, Action Action);
