namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure, Win32-free logic for the "rename network adapter" feature (issue #15): name validation,
/// uniqueness, and the deterministic InterfaceGuid → PnP-device resolution.
///
/// Kept free of any registry/SetupAPI/WinUI dependency so it can be unit-tested in isolation and
/// linked directly into the test assembly — the injection-defense point from the investigation
/// doc (§3, §5.6) lives here: the app is elevated, so the allowed-character policy and the
/// "resolve by GUID, never by name" chain are the load-bearing safety checks and are exercised by
/// tests. The actual device mutation lives in <see cref="AdapterRenamer"/>.
/// </summary>
public static class AdapterNameRules
{
    /// <summary>Maximum accepted length of a friendly name after trimming.</summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// Printable punctuation permitted in addition to Unicode letters/digits and the space
    /// character. Deliberately conservative (investigation §5.6) — excludes every shell/query
    /// metacharacter (<c>/ \ : ; " ' &amp; $ % | &lt; &gt; ` @ ! * ?</c> …) so a crafted name stays
    /// trivially safe for every downstream consumer (logs, JSON config, WMI, UI).
    /// </summary>
    public const string AllowedPunctuation = "-_()#.";

    /// <summary>Outcome of <see cref="ValidateName"/>: whether the (trimmed) name is acceptable.</summary>
    /// <param name="IsValid">True when the name passes every rule.</param>
    /// <param name="Sanitized">The trimmed candidate (use this as the value to write when valid).</param>
    /// <param name="Error">A user-facing reason when <paramref name="IsValid"/> is false; otherwise null.</param>
    public sealed record NameValidation(bool IsValid, string Sanitized, string? Error);

    /// <summary>
    /// Validates a user-supplied adapter name: trims it, then requires 1–<see cref="MaxNameLength"/>
    /// characters, no control characters, and only Unicode letters/digits, spaces, or
    /// <see cref="AllowedPunctuation"/>. Returns the trimmed candidate alongside the verdict.
    /// </summary>
    public static NameValidation ValidateName(string? input)
    {
        var trimmed = (input ?? string.Empty).Trim();

        if (trimmed.Length == 0)
            return new NameValidation(false, string.Empty, "Enter a name.");

        if (trimmed.Length > MaxNameLength)
            return new NameValidation(false, trimmed, $"Name is too long (max {MaxNameLength} characters).");

        foreach (var c in trimmed)
        {
            if (char.IsControl(c))
                return new NameValidation(false, trimmed, "Name may not contain control characters.");

            bool allowed = char.IsLetterOrDigit(c) || c == ' ' || AllowedPunctuation.IndexOf(c) >= 0;
            if (!allowed)
                return new NameValidation(false, trimmed,
                    $"The character '{c}' is not allowed. Use letters, digits, spaces, and - _ ( ) # . only.");
        }

        return new NameValidation(true, trimmed, null);
    }

    /// <summary>
    /// Read-back verdict for the device write (issue #15): a <c>FriendlyName</c> write only counts as
    /// applied when the value is now <b>present</b> on disk and <b>exactly</b> equal (ordinal) to what
    /// was written. Pure so the guard in <see cref="AdapterRenamer.WriteFriendlyName"/> — which turns
    /// the old "silent no-op reported as success" into a visible failure — is unit-testable without
    /// touching a device. The write value is already trimmed/validated by <see cref="ValidateName"/>,
    /// so an ordinal exact match is the correct persisted-vs-intended test.
    /// </summary>
    /// <param name="present">Whether a FriendlyName value exists on disk after the write.</param>
    /// <param name="onDisk">The value read back (null when absent).</param>
    /// <param name="intended">The value the caller wrote.</param>
    public static bool FriendlyNameApplied(bool present, string? onDisk, string intended)
        => present && string.Equals(onDisk, intended, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="candidate"/> does not collide (case-insensitively, ignoring
    /// surrounding whitespace) with any name in <paramref name="existingNames"/>. Windows does not
    /// enforce unique adapter descriptions, so the app must (investigation §5.5). The caller is
    /// expected to have already excluded the adapter being renamed from the list.
    /// </summary>
    public static bool IsNameUnique(string candidate, IEnumerable<string> existingNames)
    {
        var norm = (candidate ?? string.Empty).Trim();
        return !existingNames.Any(n =>
            string.Equals((n ?? string.Empty).Trim(), norm, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One row of the network-adapter Class key: an adapter's <c>NetCfgInstanceId</c> (equal to the
    /// NIC's InterfaceGuid) paired with its <c>DeviceInstanceID</c> (the PnP device to rename).
    /// </summary>
    public sealed record ClassAdapterEntry(string NetCfgInstanceId, string DeviceInstanceId);

    /// <summary>Result of <see cref="ResolveDeviceInstanceId"/>: exactly one device, or an abort reason.</summary>
    public sealed record DeviceResolution(bool Success, string? DeviceInstanceId, string? Error);

    /// <summary>
    /// Deterministically resolves a NIC's InterfaceGuid to exactly one PnP <c>DeviceInstanceID</c> by
    /// matching <c>NetCfgInstanceId</c> in the Class key — never by device name (investigation §5.2).
    /// Aborts (Success=false) when the chain resolves to zero or more than one distinct device, so a
    /// rename can never land on the wrong dock.
    /// </summary>
    public static DeviceResolution ResolveDeviceInstanceId(
        string interfaceGuid, IEnumerable<ClassAdapterEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(interfaceGuid))
            return new DeviceResolution(false, null, "The adapter has no interface GUID to resolve.");

        var guid = interfaceGuid.Trim();

        var matches = entries
            .Where(e => e is not null
                        && !string.IsNullOrWhiteSpace(e.DeviceInstanceId)
                        && string.Equals(e.NetCfgInstanceId?.Trim(), guid, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.DeviceInstanceId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matches.Count switch
        {
            1 => new DeviceResolution(true, matches[0], null),
            0 => new DeviceResolution(false, null, "No PnP device matched the adapter's GUID."),
            _ => new DeviceResolution(false, null, "The adapter's GUID resolved to more than one device."),
        };
    }
}
