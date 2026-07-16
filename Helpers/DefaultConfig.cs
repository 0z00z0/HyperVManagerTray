namespace HyperVManagerTray.Helpers;

/// <summary>
/// The canonical blank-slate <c>config.json</c> (issue #38). One string, two consumers:
/// <list type="bullet">
///   <item>the repo's shipped <c>config.json</c>, which the installer drops with
///         <c>Flags: onlyifdoesntexist</c> — i.e. only onto a machine that has no config at all; and</item>
///   <item><see cref="Services.ConfigManager.CreateDefaultIfMissing"/>, which writes it when the file
///         is absent at startup so the app self-heals instead of showing an error box and exiting.</item>
/// </list>
/// <c>DefaultConfigTests.ShippedSampleMatchesTheDefault</c> asserts the two are byte-identical, so the
/// blank slate is defined in exactly one place and the two paths cannot drift.
///
/// <para><b>Why it is empty, and why that is the point.</b> This file previously shipped a fake
/// "Office LAN" rule (MAC <c>AA:BB:CC:DD:EE:FF</c>, CIDR <c>10.0.0.0/23</c>) whose rule and fallback
/// both targeted a VM named <c>MyVM</c> that exists on no machine. That sample was not documentation —
/// it was a trap. It cost hours of live debugging once already: a real VM was never switched because
/// the placeholder <c>MyVM</c> was still sitting in the fallback's <c>targetVms</c>, and every
/// evaluation pass merely logged <c>VM 'MyVM' not found in config</c> into a file nobody was reading.
/// A shipped sample must be either genuinely inert or absent. This one is genuinely inert: no rules to
/// match, no VMs to target, and an empty <c>targetVms</c> on the fallback means the apply pass iterates
/// nothing and logs nothing (see <c>DefaultConfigTests.DefaultFallbackHasNoTargets</c>). The annotated,
/// fully-populated example lives in the README, where a wrong value can only mislead a reader — not
/// silently misconfigure a running app.</para>
/// </summary>
public static class DefaultConfig
{
    /// <summary>
    /// The default config.json text. Hand-written rather than serialised from a fresh
    /// <see cref="Models.AppConfig"/> so the shipped file has a stable, reviewable shape (key order,
    /// two-space indent) that a diff can police — a serialiser would silently reorder or add keys as
    /// the model grows. <c>DefaultConfigTests</c> asserts it round-trips to exactly the values below.
    /// </summary>
    public const string Json = """
        {
          "logLevel": "Debug",
          "virtualMachines": [],
          "rules": [],
          "fallback": {
            "virtualSwitch": "Default Switch",
            "targetVms": []
          }
        }

        """;
}
