using System.Text.Json;
using ZeroZero.Primitives;

namespace HyperVManagerTray.Helpers;

/// <summary>What the version that starts next can say about the update the previous one began.</summary>
internal enum UpdateVerdict
{
    /// <summary>No update was handed over, or its record names no usable version. The zero value.</summary>
    NothingHandedOver,

    /// <summary>The running version is the one the update was for, or newer.</summary>
    Installed,

    /// <summary>The update was started and the running version is still older than its target.</summary>
    DidNotComplete,
}

/// <summary>
/// The handover between the app and the Setup run it starts for its own update. Setup runs
/// unattended and replaces files this process holds, so the process that asked for the update is gone
/// before an outcome exists: the record left here is how the version that starts next states whether
/// the update landed.
/// </summary>
/// <remarks>The installer script writes <see cref="RefusalFileName"/>; neither side can read the
/// other's constant, so the pair is pinned by <c>Tests\UnattendedUpdateTests.cs</c>. Every member takes
/// the data folder rather than reading it, so the tests run against a temporary one.</remarks>
internal static class UnattendedUpdate
{
    /// <summary>The record the outgoing version leaves for its successor.</summary>
    internal const string HandoverFileName = "update-handover.json";

    /// <summary>Setup's own reason for installing nothing, written by the installer script at the one
    /// point it refuses. One ASCII line.</summary>
    internal const string RefusalFileName = "update-refused.txt";

    /// <summary>Longest refusal carried into the report. The balloon that shows it holds 255
    /// characters, and a longer text is dropped whole rather than shortened.</summary>
    internal const int MaxRefusalLength = 80;

    /// <summary>The version an update was started for, and when.</summary>
    internal sealed record Handover
    {
        /// <summary>Three-part, as the release tag states it.</summary>
        public string TargetVersion { get; init; } = "";

        /// <summary>Round-trip UTC. Kept for reading the file by hand.</summary>
        public string StartedUtc { get; init; } = "";

        /// <summary>
        /// Whether nobody asked for this update — the automatic install, rather than the tray's
        /// "Check for updates". The one thing that tells the two paths apart, and it is written down
        /// because nothing else here can: the version, the time and the refusal are identical either
        /// way, and the process that could have said which is the one Setup had to close.
        /// </summary>
        /// <remarks>False in a record an older version wrote, which is correct: automatic installing
        /// did not exist, so every such record came from someone pressing the menu item.</remarks>
        public bool Automatic { get; init; }
    }

    /// <summary>What the next start reports about one attempt.</summary>
    internal sealed record Outcome(
        UpdateVerdict Verdict, string TargetVersion, string RunningVersion, string? Refusal, bool Automatic);

    /// <summary>Records the attempt. Called once Setup is running and before this process exits: Setup
    /// waits for that exit before it installs anything, so the record is always on disk first. Never
    /// throws — a record that cannot be written must not stop the update.</summary>
    /// <param name="automatic">True where nobody asked — the automatic install. Stated by the caller
    /// rather than guessed at here: it is the caller that knows which path it is on, and neither the
    /// timing of the write nor whether a window was open can tell the two apart.</param>
    internal static void Record(string dataDir, string targetVersion, bool automatic, DateTimeOffset now, ILogSink log)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            // A refusal from an earlier attempt says nothing about this one.
            Discard(Path.Combine(dataDir, RefusalFileName), log);
            File.WriteAllText(Path.Combine(dataDir, HandoverFileName), JsonSerializer.Serialize(new Handover
            {
                TargetVersion = targetVersion,
                StartedUtc    = now.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Automatic     = automatic,
            }));
        }
        catch (Exception ex)
        {
            log.Error("UnattendedUpdate.Record", ex);
        }
    }

    /// <summary>
    /// Reads the handed-over attempt, if any, and clears it. Null where nothing was handed over — the
    /// ordinary start. Never throws.
    /// </summary>
    /// <remarks>Cleared whatever it holds, a damaged record included: the record is one attempt, not a
    /// standing state, and one that could not be read would otherwise be re-read at every start.</remarks>
    internal static Outcome? Take(string dataDir, string runningVersion, ILogSink log)
    {
        var handoverPath = Path.Combine(dataDir, HandoverFileName);
        try
        {
            if (!File.Exists(handoverPath)) return null;
        }
        catch (Exception ex)
        {
            log.Error("UnattendedUpdate.Take", ex);
            return null;
        }

        Handover? handover = null;
        try
        {
            handover = JsonSerializer.Deserialize<Handover>(File.ReadAllText(handoverPath));
        }
        catch (Exception ex)
        {
            log.Error("UnattendedUpdate.Take", ex);
        }

        var refusal = ReadRefusal(dataDir, log);
        Discard(handoverPath, log);
        Discard(Path.Combine(dataDir, RefusalFileName), log);

        var verdict = VerdictFor(handover?.TargetVersion, runningVersion);
        if (verdict == UpdateVerdict.NothingHandedOver)
        {
            log.Info($"Unattended update: a record was found but names no usable version ('{handover?.TargetVersion}'); nothing reported.");
            return null;
        }

        return new Outcome(verdict, handover!.TargetVersion, runningVersion, refusal, handover.Automatic);
    }

    /// <summary>
    /// What the running version can say about the handed-over attempt. The version comparison is the
    /// evidence, not the installer's exit code: the process that could have read that code is the one
    /// Setup had to close.
    /// </summary>
    internal static UpdateVerdict VerdictFor(string? targetVersion, string runningVersion)
    {
        if (targetVersion is not { Length: > 0 }) return UpdateVerdict.NothingHandedOver;

        // An unparseable version on either side is no evidence of a failure, so it reports none.
        if (!Version.TryParse(targetVersion.TrimStart('v'), out var target) ||
            !Version.TryParse(runningVersion.TrimStart('v'), out var running))
            return UpdateVerdict.NothingHandedOver;

        return running >= target ? UpdateVerdict.Installed : UpdateVerdict.DidNotComplete;
    }

    /// <summary>Setup's stated reason, first line only and capped, or null where it wrote none — which
    /// includes every failure that never reached its refusal point.</summary>
    private static string? ReadRefusal(string dataDir, ILogSink log)
    {
        try
        {
            var path = Path.Combine(dataDir, RefusalFileName);
            if (!File.Exists(path)) return null;

            var line = File.ReadAllText(path).Trim().Split('\n')[0].Trim();
            if (line.Length == 0) return null;
            return line.Length <= MaxRefusalLength ? line : line[..(MaxRefusalLength - 1)] + "…";
        }
        catch (Exception ex)
        {
            log.Error("UnattendedUpdate.ReadRefusal", ex);
            return null;
        }
    }

    private static void Discard(string path, ILogSink log)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { log.Error("UnattendedUpdate.Discard", ex); }
    }
}
