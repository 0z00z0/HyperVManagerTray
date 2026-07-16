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
    /// Validates a candidate description against every in-dialog rule at once (issue #40): the
    /// character/length policy of <see cref="ValidateName"/>, then the no-op check against the
    /// adapter's current description, then uniqueness against the other adapters.
    ///
    /// <para>Pure and combined so the dialog can render ONE inline error as the user types rather than
    /// stacking a message box per failed rule — #40 collapses the consent stack, and a validation
    /// message box is still a box. The order matters: an invalid name is reported as invalid before it
    /// is ever compared to anything, so the user sees the reason closest to what they typed.</para>
    /// </summary>
    /// <param name="input">The raw text from the dialog's input box.</param>
    /// <param name="currentDescription">The adapter's current description (the no-op comparand).</param>
    /// <param name="otherDescriptions">Every OTHER adapter's description (the uniqueness comparands).</param>
    public static NameValidation ValidateNewName(
        string? input, string currentDescription, IEnumerable<string> otherDescriptions)
    {
        var validation = ValidateName(input);
        if (!validation.IsValid) return validation;

        if (string.Equals(validation.Sanitized, currentDescription, StringComparison.Ordinal))
            return new NameValidation(false, validation.Sanitized,
                "That is already this adapter's description — nothing to change.");

        if (!IsNameUnique(validation.Sanitized, otherDescriptions))
            return new NameValidation(false, validation.Sanitized,
                "Another adapter already has that description. Choose a unique one.");

        return validation;
    }

    /// <summary>What the user is told once the rename/reset has run to completion (issue #40).</summary>
    public enum RenameOutcomeKind
    {
        /// <summary>The device was restarted AND the description was re-read from disk and matches.</summary>
        AppliedVerified,

        /// <summary>Written and verified on disk, but no restart was requested — not live everywhere yet.</summary>
        SavedRestartPending,

        /// <summary>The device restarted, but the description on disk is NOT what was written.</summary>
        VerificationFailed,

        /// <summary>The description was written, but the device restart itself threw.</summary>
        RestartFailed,
    }

    /// <summary>An outcome verdict plus the exact text to show for it.</summary>
    /// <param name="Kind">Which outcome occurred.</param>
    /// <param name="Message">The user-facing text.</param>
    public sealed record RenameOutcome(RenameOutcomeKind Kind, string Message)
    {
        /// <summary>
        /// True <b>only</b> for <see cref="RenameOutcomeKind.AppliedVerified"/> — the single outcome in
        /// which the description was re-read from disk after the restart and matched. This is the one
        /// state that may be presented to the user as "it worked"; nothing else may claim it.
        /// </summary>
        public bool IsSuccess => Kind == RenameOutcomeKind.AppliedVerified;

        /// <summary>
        /// True when something went wrong and the user has to act. Deliberately NOT the negation of
        /// <see cref="IsSuccess"/>: <see cref="RenameOutcomeKind.SavedRestartPending"/> is neither a
        /// success nor a problem — the user chose it — so it is reported plainly rather than with a
        /// warning icon it does not deserve.
        /// </summary>
        public bool NeedsAttention =>
            Kind is RenameOutcomeKind.VerificationFailed or RenameOutcomeKind.RestartFailed;
    }

    /// <summary>
    /// The outcome after a device restart (issue #40, preserving issue #15's rule): reports success
    /// <b>only</b> when the description was re-read from disk after the restart and exactly matches what
    /// was written. Every other state — absent, or present but different — is a
    /// <see cref="RenameOutcomeKind.VerificationFailed"/> warning naming what is actually on disk.
    ///
    /// <para>This is the single gate between "we attempted a rename" and "we told the user it worked",
    /// pulled out as a pure function precisely so a test can prove an unverified rename can never be
    /// reported as success. It delegates the comparison to <see cref="FriendlyNameApplied"/> rather than
    /// re-implementing it, so the write-time guard and the restart-time guard cannot drift apart.</para>
    /// </summary>
    /// <param name="present">Whether a FriendlyName exists on disk after the restart.</param>
    /// <param name="onDisk">The value re-read after the restart (null when absent).</param>
    /// <param name="intended">The description that was written.</param>
    public static RenameOutcome DescribeRestartOutcome(bool present, string? onDisk, string intended)
        => FriendlyNameApplied(present, onDisk, intended)
            ? new RenameOutcome(RenameOutcomeKind.AppliedVerified,
                $"The adapter was restarted and its description is now \"{intended}\" everywhere.")
            : new RenameOutcome(RenameOutcomeKind.VerificationFailed,
                "The adapter was restarted, but its description on disk is now " +
                (present ? $"\"{onDisk}\"" : "absent") +
                $" instead of \"{intended}\" — Windows may have reset it. Try again, or reboot to re-apply.");

    /// <summary>
    /// The outcome when the user left the restart checkbox unticked (issue #40). The description IS
    /// verified on disk at this point — <see cref="AdapterRenamer.WriteFriendlyName"/> throws otherwise
    /// — so this is an honest "saved", never a claim that it is live everywhere.
    /// </summary>
    public static RenameOutcome DescribeDeferredOutcome(string intended)
        => new(RenameOutcomeKind.SavedRestartPending,
            $"The adapter's description is saved as \"{intended}\".\n\n" +
            "Some places will keep showing the old description until the adapter is restarted or the PC reboots.");

    /// <summary>
    /// The outcome when the restart itself threw (issue #40). The write was already verified on disk, so
    /// this reports exactly that and nothing more — the description is saved, but it is NOT live.
    /// </summary>
    public static RenameOutcome DescribeRestartFailure(string intended, string error)
        => new(RenameOutcomeKind.RestartFailed,
            $"The adapter's description is saved as \"{intended}\", but the adapter could not be restarted " +
            $"automatically:\n{error}\n\nRestart the adapter manually or reboot to apply it.");

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
    /// Fallback shown when an adapter has neither a FriendlyName nor a description — matches the
    /// "—" unknown-value sentinel the rest of the UI already uses. Unreachable in practice
    /// (<c>NetworkInterface.Description</c> is never empty), but guarantees a blank is never displayed.
    /// </summary>
    public const string UnknownDisplayName = "—";

    /// <summary>
    /// Decides which string to DISPLAY for an adapter (issue #32): the device's <c>FriendlyName</c>
    /// when it has one, otherwise the adapter's <c>NetworkInterface.Description</c>.
    ///
    /// <para><b>Why this exists.</b> The rename writes the PnP device's <c>FriendlyName</c>, which
    /// Windows surfaces as the NDIS <c>InterfaceDescription</c>. The app used to display
    /// <see cref="System.Net.NetworkInformation.NetworkInterface"/>'s <c>Description</c> — a
    /// *different* property, sourced from the IP Helper API and derived from the driver's
    /// <c>DeviceDesc</c> plus a <c>#N</c> dedupe suffix, which <c>FriendlyName</c> does not affect. The
    /// app therefore renamed one string and displayed another, so a rename never appeared anywhere in
    /// the UI (#32, the un-fixed half of #15).</para>
    ///
    /// <para>Pure so the whole decision — including every degrade-safely path — is unit-testable
    /// without a live NIC or registry. The caller passes <paramref name="friendlyName"/> as null
    /// whenever the FriendlyName is unavailable for ANY reason: the device resolved to zero or more
    /// than one entry, the Enum key was unreadable, or the device simply has no explicit FriendlyName
    /// (the real, already-modelled <c>Present=false</c> case). Every one of those degrades to
    /// <paramref name="fallbackDescription"/> rather than throwing or blanking.</para>
    ///
    /// <para><b>Display only.</b> The result must never be fed to rule matching or to the picker's
    /// software-adapter gate — both must keep reading the raw, un-renamable
    /// <c>NetworkInterface.Description</c>. See <c>AdapterMatcher</c>.</para>
    /// </summary>
    /// <param name="friendlyName">The device's FriendlyName, or null when absent/ambiguous/unreadable.</param>
    /// <param name="fallbackDescription">The adapter's <c>NetworkInterface.Description</c>.</param>
    public static string ChooseDisplayName(string? friendlyName, string? fallbackDescription)
    {
        var friendly = (friendlyName ?? string.Empty).Trim();
        if (friendly.Length > 0) return friendly;

        var fallback = (fallbackDescription ?? string.Empty).Trim();
        return fallback.Length > 0 ? fallback : UnknownDisplayName;
    }

    /// <summary>
    /// Extracts the device's FACTORY description from a raw <c>DeviceDesc</c> registry value
    /// (issue #33), or null when no usable literal can be derived.
    ///
    /// <para><b>Why this exists.</b> The saved "original" adapter name used to be captured by reading
    /// whatever <c>FriendlyName</c> was on disk at first-rename time. That is correct on a true first
    /// rename, but the two stores have different lifetimes: the rename lives in the registry (survives
    /// an uninstall) while the config record lives next to the exe (does not). After an
    /// uninstall/reinstall — or a wiped/hand-edited config — the flow sees no record while
    /// <c>FriendlyName</c> already holds a PREVIOUS rename's output, and faithfully records that as
    /// "the original". Reset then restores the very name the user was escaping. <c>DeviceDesc</c> sits
    /// on the same Enum key, is never touched by the rename, and therefore is the actual ground truth.</para>
    ///
    /// <para><b>The two forms.</b> <c>DeviceDesc</c> is either an INF-indirect string
    /// <c>@oem241.inf,%rtl8153.devicedesc%;Realtek USB GbE Family Controller</c> — an <c>@inf,%key%</c>
    /// prefix, a <c>;</c>, then the literal Windows falls back on — or a plain literal with no prefix
    /// at all (<c>Realtek USB GbE Family Controller</c>). Both are common; the parse must handle both.</para>
    ///
    /// <para><b>The rule.</b> Only a leading <c>@</c> marks the indirect form, so the literal is taken
    /// as everything after the FIRST <c>;</c> — the format has exactly one separator, so a <c>;</c>
    /// appearing INSIDE the display name is part of the name and must survive (splitting on the LAST
    /// <c>;</c> would truncate it). Without a leading <c>@</c> the whole trimmed value IS the literal
    /// and is returned verbatim, <c>;</c> and all. Every unusable shape — null, empty, whitespace, an
    /// indirect form with no <c>;</c> (nothing to fall back to), a trailing <c>;</c> with nothing after
    /// it, or a value carrying control characters — returns null rather than a blank or a garbage
    /// <c>@inf,%key%</c> string, so the caller can degrade to its own fallback (see
    /// <see cref="CaptureOriginal"/>). Never returns an empty or whitespace-only string.</para>
    ///
    /// <para>Pure so the whole parse is unit-testable with no live NIC and no registry. The read
    /// itself lives in <see cref="AdapterDeviceRegistry.ReadDeviceDesc"/> and is READ-only.</para>
    /// </summary>
    /// <param name="deviceDesc">The raw <c>DeviceDesc</c> value, or null when absent/unreadable.</param>
    public static string? ParseFactoryDescription(string? deviceDesc)
    {
        var value = (deviceDesc ?? string.Empty).Trim();
        if (value.Length == 0) return null;

        if (value[0] == '@')
        {
            // INF-indirect: @<inf>,%<strkey>%;<literal>. The literal is everything past the FIRST ';'.
            int semi = value.IndexOf(';');
            if (semi < 0) return null;              // no literal to fall back on — unusable
            value = value[(semi + 1)..].Trim();
            if (value.Length == 0) return null;     // trailing ';' with nothing after it
        }

        // Defensive: a control character here would be bizarre. Refuse rather than return it, so the
        // caller falls back instead of writing it to the device on a later Reset.
        foreach (var c in value)
            if (char.IsControl(c)) return null;

        return value;
    }

    /// <summary>
    /// What to persist as an adapter's "original" name: the value to restore on Reset, and whether the
    /// device had nothing to restore at all.
    /// </summary>
    /// <param name="OriginalFriendlyName">The name Reset should write back (empty when there is none).</param>
    /// <param name="OriginalWasAbsent">True when nothing can be restored, so Reset must not be offered.</param>
    public sealed record OriginalCapture(string OriginalFriendlyName, bool OriginalWasAbsent);

    /// <summary>
    /// Decides what to record as the "original" on an adapter's first rename (issue #33): the
    /// <c>DeviceDesc</c>-derived factory description when one could be derived, otherwise a safe
    /// fallback to the pre-#33 behaviour.
    ///
    /// <para><b>Preferred path.</b> A non-empty <paramref name="factoryDescription"/> is ground truth —
    /// it is never touched by a rename, so it cannot be a previous rename's output. Recorded with
    /// <c>OriginalWasAbsent=false</c> even when the device carries no explicit <c>FriendlyName</c>:
    /// Windows displays the <c>DeviceDesc</c> literal in that case, so writing that same literal back
    /// on Reset reproduces the original display exactly. That deliberately makes Reset AVAILABLE where
    /// it previously was not, and it is still never a delete (§5.4).</para>
    ///
    /// <para><b>Fallback path.</b> When the factory description is missing/unreadable/unparseable, this
    /// degrades to exactly the pre-#33 behaviour: record the on-disk <c>FriendlyName</c>, or mark the
    /// original absent when there is none. That path can still capture a prior rename's output as "the
    /// original" — it is strictly no worse than today, and it is the only thing left to record once
    /// ground truth is unavailable. It never throws and never records a blank as if it were a name.</para>
    ///
    /// <para>Pure, so both paths are unit-testable without a device.</para>
    /// </summary>
    /// <param name="factoryDescription">Result of <see cref="ParseFactoryDescription"/>; null when underivable.</param>
    /// <param name="friendlyNamePresent">Whether the device has an explicit <c>FriendlyName</c> on disk.</param>
    /// <param name="friendlyName">That <c>FriendlyName</c> (null when absent).</param>
    public static OriginalCapture CaptureOriginal(
        string? factoryDescription, bool friendlyNamePresent, string? friendlyName)
    {
        var factory = (factoryDescription ?? string.Empty).Trim();
        if (factory.Length > 0)
            return new OriginalCapture(factory, false);

        return friendlyNamePresent
            ? new OriginalCapture(friendlyName ?? string.Empty, false)
            : new OriginalCapture(string.Empty, true);
    }

    /// <summary>
    /// Repairs an ALREADY-STORED "original" that was captured before issue #33 was fixed, or null when
    /// the record must be left untouched.
    ///
    /// <para><b>The damage.</b> Records written by the old capture path can hold a previous rename's
    /// output as the "original" — on the reporting machine, <c>originalFriendlyName</c> and
    /// <c>currentFriendlyName</c> are both <c>"Dell docking (Petterhaugen)"</c> while the true original
    /// is <c>"Realtek USB GbE Family Controller"</c>. Reset is a no-op there, and the factory name is
    /// unrecoverable through the app. Those records need correcting in place; a fix that only helps
    /// future renames leaves them broken forever.</para>
    ///
    /// <para><b>The rule.</b> Repair whenever the factory description can be POSITIVELY re-derived and
    /// the stored original differs from it; leave the record completely untouched when it cannot
    /// (<paramref name="factoryDescription"/> null/empty) or when it already matches. This is broader
    /// than matching only the poisoned <c>original == current</c> signature — that narrower test misses
    /// a record poisoned and then renamed again — and it establishes one self-healing invariant: the
    /// stored original equals the factory description whenever that description is derivable, which is
    /// exactly what <see cref="CaptureOriginal"/> now writes.</para>
    ///
    /// <para><b>The accepted trade-off.</b> An original that is genuinely a pre-existing custom
    /// <c>FriendlyName</c> — set by an OEM or by hand BEFORE this app ever renamed the device — also
    /// differs from the factory description and is therefore also rewritten to it. That case is
    /// indistinguishable from the poisoned one (both are "a non-factory name in the original slot"),
    /// and the outcome is benign: Reset restores the factory description, which is what issue #33's
    /// acceptance criteria ask for, and it is never a delete. The conservative half of the rule is the
    /// part that matters — a record whose ground truth cannot be read is never guessed at.</para>
    /// </summary>
    /// <param name="stored">The currently persisted original.</param>
    /// <param name="factoryDescription">Result of <see cref="ParseFactoryDescription"/>; null when underivable.</param>
    /// <returns>The corrected capture, or null to leave the stored record alone.</returns>
    public static OriginalCapture? RepairOriginal(OriginalCapture stored, string? factoryDescription)
    {
        var factory = (factoryDescription ?? string.Empty).Trim();
        if (factory.Length == 0) return null;   // no ground truth — never guess

        if (!stored.OriginalWasAbsent
            && string.Equals(stored.OriginalFriendlyName, factory, StringComparison.Ordinal))
            return null;                        // already correct

        return new OriginalCapture(factory, false);
    }

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
