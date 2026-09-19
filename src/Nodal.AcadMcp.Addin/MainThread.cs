using System.Collections.Concurrent;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nodal.AcadMcp.Addin;

/// <summary>
/// Marshals work from HttpListener worker threads onto AutoCAD's main thread.
///
/// AutoCAD's Database/Editor are hard-bound to the main thread; touching them from a worker
/// is an eFatalError-class crash, not an exception. Application.Idle is raised on the main
/// thread, so it is the one reliable place to drain queued work.
/// </summary>
internal static class MainThread
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static bool _hooked;

    /// <summary>Work items drained per Idle tick. Bounded so a burst can't freeze the UI.</summary>
    private const int DrainBudget = 8;

    internal static void Start()
    {
        if (_hooked) return;
        AcApp.Idle += OnIdle;
        _hooked = true;
    }

    internal static void Stop()
    {
        if (!_hooked) return;
        AcApp.Idle -= OnIdle;
        _hooked = false;

        // Fail anything still queued so no caller hangs forever.
        while (Queue.TryDequeue(out var orphan))
        {
            try { orphan(); } catch { /* the work item owns its own error reporting */ }
        }
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        var budget = DrainBudget;
        while (budget-- > 0 && Queue.TryDequeue(out var work))
        {
            // Each work item captures its own exceptions into its TaskCompletionSource.
            // A throw escaping here would tear down AutoCAD's idle loop.
            try { work(); } catch { }
        }
    }

    internal static Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    internal static Task InvokeAsync(Action action) =>
        InvokeAsync<object?>(() => { action(); return null; });

    /// <summary>
    /// Runs work in AutoCAD's DOCUMENT/COMMAND context rather than application context.
    ///
    /// Application.Idle fires in application context, which is fine for Database work but is
    /// rejected by anything that needs the command loop — acedInvoke (Application.Invoke, i.e.
    /// AutoLISP evaluation) returns eInvalidInput there. ExecuteInCommandContextAsync is the
    /// documented hop; we still have to reach the main thread via the Idle pump first, because
    /// ExecuteInCommandContextAsync may only be called from the main thread itself.
    /// </summary>
    internal static Task<T> InCommandContextAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Queue.Enqueue(() =>
        {
            try
            {
                var docs = AcApp.DocumentManager;

                // If AutoCAD already has us in document context, just run it.
                if (!docs.IsApplicationContext)
                {
                    tcs.TrySetResult(func());
                    return;
                }

                // Fire-and-forget: the TaskCompletionSource is what the caller awaits.
                _ = docs.ExecuteInCommandContextAsync(_ =>
                {
                    try { tcs.TrySetResult(func()); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                    return Task.CompletedTask;
                }, null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }
}
