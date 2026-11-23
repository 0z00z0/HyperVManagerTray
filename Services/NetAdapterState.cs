using System.Globalization;
using System.Management;
using HyperVManagerTray.Helpers;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

/// <summary>Connected hardware cards from <c>MSFT_NetAdapter</c> (root\StandardCimv2), read-only and
/// readable without elevation; filter and QoS copies of a card are not rows of that class.</summary>
internal sealed class NetAdapterState(ILogger logger)
{
    // The whole read, connect included.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private static readonly object Gate = new();
    // At most one read runs, so an abandoned read holds at most one pool thread.
    private static Task<(int Rows, HashSet<Guid> Cards)>? _read;
    // Only a change of availability is logged.
    private static bool _unavailable;

    /// <summary>The connected cards, or <see cref="CardState.Unavailable"/>. BLOCKING; never throws.</summary>
    public CardState ReadConnectedCards()
    {
        try
        {
            Task<(int Rows, HashSet<Guid> Cards)> read;
            lock (Gate)
            {
                if (_read is { IsCompleted: false }) return Unavailable("an earlier read has not finished");
                read = _read = Task.Run(Query);
            }

            // The wait handle rather than Task.Wait, which would wrap a fault in an AggregateException.
            if (!((IAsyncResult)read).AsyncWaitHandle.WaitOne(Limit))
            {
                _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return Unavailable($"no answer within {Limit.TotalSeconds:N0} s");
            }

            var (rows, cards) = read.GetAwaiter().GetResult();
            if (rows == 0) return Unavailable("no adapters listed");

            lock (Gate)
            {
                if (_unavailable) logger.LogInformation("Network card state from MSFT_NetAdapter available again");
                _unavailable = false;
            }
            return new CardState(true, cards);
        }
        catch (Exception ex)
        {
            return Unavailable(ex.Message);
        }
    }

    private CardState Unavailable(string reason)
    {
        lock (Gate)
        {
            if (!_unavailable)
                logger.LogWarning("Network card state from MSFT_NetAdapter unavailable ({Reason}); the network is not acted on until it returns", reason);
            _unavailable = true;
        }
        return CardState.Unavailable;
    }

    private static (int Rows, HashSet<Guid> Cards) Query()
    {
        var scope = new ManagementScope(@"\\.\root\StandardCimv2");
        scope.Connect();
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
            "SELECT InterfaceGuid, HardwareInterface, Virtual, MediaConnectState FROM MSFT_NetAdapter"),
            WmiLimits.Enumeration());
        using var results = s.Get();   // Rewindable collection: its own enumerator to release

        var rows  = 0;
        var cards = new HashSet<Guid>();
        foreach (ManagementObject o in results)
            using (o)
            {
                rows++;
                try
                {
                    // MediaConnectState 1 is Connected. Virtual is true for the vEthernet adapter that clones the card's MAC.
                    if (o["HardwareInterface"] is true && o["Virtual"] is false
                        && Convert.ToUInt32(o["MediaConnectState"], CultureInfo.InvariantCulture) == 1
                        && Guid.TryParse(o["InterfaceGuid"] as string, out var id))
                        cards.Add(id);
                }
                catch { /* one unreadable row is skipped, not the whole list */ }
            }
        return (rows, cards);
    }
}
