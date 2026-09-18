using System.Management;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Time limits for Hyper-V WMI calls (issue #114). With vmms stopped, or stuck in "stopping", a WMI call
/// into its namespace can wait indefinitely; without a limit that wait holds the refresh lock and every
/// refresh queued behind it. Each call here gives up after a bounded time and throws, which the callers
/// already handle as an ordinary failed read or action.
/// </summary>
internal static class WmiLimits
{
    /// <summary>Connecting to the Hyper-V namespace.</summary>
    public static readonly TimeSpan Connect = TimeSpan.FromSeconds(15);

    /// <summary>Each step of a query's enumeration, and a single object read.</summary>
    public static readonly TimeSpan Read = TimeSpan.FromSeconds(30);

    /// <summary>A method call. Long-running Hyper-V work returns a job at once, so this bounds the request only.</summary>
    public static readonly TimeSpan Invoke = TimeSpan.FromSeconds(60);

    /// <summary>Options for a <see cref="ManagementObjectSearcher"/>; the rest match the searcher's defaults.</summary>
    public static System.Management.EnumerationOptions Enumeration() => new() { Timeout = Read };

    /// <summary>Options for <see cref="ManagementObject.InvokeMethod(string, ManagementBaseObject, InvokeMethodOptions)"/>.</summary>
    public static InvokeMethodOptions Method() => new() { Timeout = Invoke };

    /// <summary>Options for a <see cref="ManagementObject"/> read by path.</summary>
    public static ObjectGetOptions Get() => new() { Timeout = Read };

    /// <summary>
    /// Connects <paramref name="scope"/>, giving up after <see cref="Connect"/>. The connect itself has no
    /// time limit of its own, so it runs on the thread pool and is abandoned on timeout; an abandoned
    /// connect only ever produces a scope nobody holds.
    /// </summary>
    /// <exception cref="TimeoutException">The connect did not finish in time.</exception>
    public static void ConnectBounded(ManagementScope scope)
    {
        var connect = Task.Run(scope.Connect);
        // The wait handle rather than Task.Wait, which would throw a fault wrapped in an AggregateException.
        if (!((IAsyncResult)connect).AsyncWaitHandle.WaitOne(Connect))
        {
            // Observe a late failure so it never surfaces as an unobserved task exception.
            _ = connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException($"Connecting to Hyper-V did not finish within {Connect.TotalSeconds:N0} s.");
        }
        connect.GetAwaiter().GetResult();   // rethrow a connect failure as itself, not an AggregateException
    }
}
