using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>One stored name that could not be turned into an identifier.</summary>
/// <param name="Where">What holds it: a rule, the fallback, a managed VM.</param>
/// <param name="What">What it names: a switch, a VM, a network adapter.</param>
/// <param name="Name">The stored name.</param>
/// <param name="Matches">How many objects on the host carry that name: zero or several. Null when the host
/// could not be read, so nothing is known yet.</param>
public sealed record UnresolvedName(string Where, string What, string Name, int? Matches)
{
    /// <summary>A stable key, so the same item is reported once however often it is seen.</summary>
    public string Key => $"{Where}\n{What}\n{Name}";

    /// <summary>The item as one sentence, for the log and the settings window.</summary>
    public string Describe() => Matches switch
    {
        null => $"{Where}: {What} '{Name}' is not identified yet — the host could not be read.",
        0    => $"{Where}: no {What} on this host is called '{Name}'.",
        _    => $"{Where}: {Matches} of this host's {What}s are called '{Name}', so it cannot be told which one is meant.",
    };
}

/// <summary>What a migration pass decided.</summary>
/// <param name="Config">The configuration to write. A copy: the one passed in is never changed.</param>
/// <param name="Changed">Whether <paramref name="Config"/> differs from the input and must be written.</param>
/// <param name="Resolved">One line per name that became an identifier, for the log.</param>
/// <param name="Unresolved">Every name still waiting for an identifier.</param>
public sealed record IdentityMigrationResult(
    AppConfig Config, bool Changed, IReadOnlyList<string> Resolved, IReadOnlyList<UnresolvedName> Unresolved);

/// <summary>
/// Turns the display names an older settings document holds into the identifiers the host assigns. Pure:
/// the host arrives as a snapshot, so the rule it applies — a name matching exactly one object is
/// migrated, a name matching none or several is kept and reported, never guessed — is testable without a
/// host.
/// </summary>
/// <remarks>
/// Nothing is concluded from a host that could not be read: a stopped Virtual Machine Management lists no
/// VMs, and that must not read as "no VM has this name". Rule IDs need no host, so a rule the load gave a
/// fresh ID is written down either way.
/// </remarks>
public static class ConfigIdentityMigration
{
    /// <summary>Whether <paramref name="config"/> holds anything this migration could still act on.</summary>
    public static bool HasWork(AppConfig config) =>
        config.Rules.Any(r => r.IdGeneratedOnLoad || string.IsNullOrWhiteSpace(r.Id))
        || Outstanding(config).Count > 0
        || config.VirtualMachines.Any(v => !string.IsNullOrWhiteSpace(v.Id) && v.NicId is null);

    /// <summary>
    /// The names still waiting for an identifier, read from the configuration alone. What the settings
    /// window lists as needing attention. An adapter left unidentified needs the host to judge, so it is
    /// not listed here; the VM's card says so where the host has been read.
    /// </summary>
    public static IReadOnlyList<UnresolvedName> Outstanding(AppConfig config)
    {
        var list = new List<UnresolvedName>();
        foreach (var vm in config.VirtualMachines.Where(v => string.IsNullOrWhiteSpace(v.Id)))
            list.Add(new UnresolvedName("Managed VMs", "VM", vm.Name, null));
        foreach (var (where, target) in Targets(config))
        {
            if (!string.IsNullOrWhiteSpace(target.LegacyVirtualSwitch))
                list.Add(new UnresolvedName(where, "virtual switch", target.LegacyVirtualSwitch!, null));
            foreach (var name in target.LegacyTargetVms ?? [])
                list.Add(new UnresolvedName(where, "VM", name, null));
        }
        return list;
    }

    /// <summary>Resolves every stored name against <paramref name="host"/>.</summary>
    public static IdentityMigrationResult Plan(AppConfig current, HyperVInventory host)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(host);

        var config     = Copy(current);
        var resolved   = new List<string>();
        var unresolved = new List<UnresolvedName>();
        bool changed   = false;

        foreach (var rule in config.Rules)
        {
            if (rule.IdGeneratedOnLoad || string.IsNullOrWhiteSpace(rule.Id)) changed = true;
            if (string.IsNullOrWhiteSpace(rule.Id)) rule.Id = NewRuleId();
            rule.IdGeneratedOnLoad = false;
        }

        if (!host.Readable)
        {
            unresolved.AddRange(Outstanding(config));
            return new IdentityMigrationResult(config, changed, resolved, unresolved);
        }

        // Managed VMs first: a rule's target is then checked against the migrated list.
        var kept = new List<VmTarget>();
        foreach (var vm in config.VirtualMachines)
        {
            if (string.IsNullOrWhiteSpace(vm.Id))
            {
                var matches = host.Vms.Where(h => NameIs(h.Name, vm.Name)).ToList();
                if (matches.Count != 1)
                {
                    unresolved.Add(new UnresolvedName("Managed VMs", "VM", vm.Name, matches.Count));
                    kept.Add(vm);
                    continue;
                }

                changed = true;
                var id = HostIdentity.Bare(matches[0].Id);
                if (kept.Any(k => HostIdentity.Same(k.Id, id)))
                {
                    // The same VM listed twice under its name: the entry already identified stands.
                    resolved.Add($"Managed VM '{vm.Name}' is {id}, already managed — the second entry is dropped");
                    continue;
                }
                vm.Id   = id;
                vm.Name = matches[0].Name;
                resolved.Add($"Managed VM '{vm.Name}' is {id}");
            }

            ResolveNic(vm, host, resolved, unresolved, ref changed);
            kept.Add(vm);
        }
        config.VirtualMachines = kept;

        foreach (var (where, target) in Targets(config))
        {
            if (!string.IsNullOrWhiteSpace(target.LegacyVirtualSwitch))
            {
                var name    = target.LegacyVirtualSwitch!;
                var matches = host.Switches.Where(s => NameIs(s.Name, name)).ToList();
                if (matches.Count == 1)
                {
                    target.SwitchId            = HostIdentity.Bare(matches[0].Id);
                    target.SwitchName          = matches[0].Name;
                    target.LegacyVirtualSwitch = null;
                    changed = true;
                    resolved.Add($"{where}: virtual switch '{name}' is {target.SwitchId}");
                }
                else
                {
                    unresolved.Add(new UnresolvedName(where, "virtual switch", name, matches.Count));
                }
            }

            if (target.LegacyTargetVms is { Count: > 0 } names)
            {
                var left = new List<string>();
                foreach (var name in names)
                {
                    var matches = host.Vms.Where(h => NameIs(h.Name, name)).ToList();
                    if (matches.Count != 1)
                    {
                        unresolved.Add(new UnresolvedName(where, "VM", name, matches.Count));
                        left.Add(name);
                        continue;
                    }
                    var id = HostIdentity.Bare(matches[0].Id);
                    if (!target.TargetVmIds.Any(t => HostIdentity.Same(t, id))) target.TargetVmIds.Add(id);
                    changed = true;
                    resolved.Add($"{where}: VM '{name}' is {id}");
                }
                target.LegacyTargetVms = left.Count > 0 ? left : null;
                if (left.Count != names.Count) changed = true;
            }
            else if (target.LegacyTargetVms is { Count: 0 })
            {
                // An empty legacy list carries nothing; dropping it keeps the file in its current shape.
                target.LegacyTargetVms = null;
                changed = true;
            }
        }

        return new IdentityMigrationResult(config, changed, resolved, unresolved);
    }

    /// <summary>
    /// Identifies a managed VM's adapter from its stored name: exactly one adapter of that name is taken.
    /// A VM with a single adapter needs no identifier — "the only adapter" is already unambiguous — so only a
    /// VM with several reports a name it cannot place.
    /// </summary>
    private static void ResolveNic(
        VmTarget vm, HyperVInventory host, List<string> resolved, List<UnresolvedName> unresolved, ref bool changed)
    {
        if (vm.NicId is not null) return;
        var nics = host.NicsFor(vm.Id);
        if (nics.Count == 0) return;   // the VM is not on this host, or has no adapter: nothing to decide

        var matches = nics.Where(n => NameIs(n.Name, vm.NicName)).ToList();
        if (matches.Count == 1)
        {
            vm.NicId   = HostIdentity.Bare(matches[0].Id);
            vm.NicName = matches[0].Name;
            changed = true;
            resolved.Add($"Managed VM '{vm.Name}': network adapter '{vm.NicName}' is {vm.NicId}");
        }
        else if (nics.Count > 1)
        {
            unresolved.Add(new UnresolvedName($"Managed VM '{vm.Name}'", "network adapter", vm.NicName, matches.Count));
        }
    }

    /// <summary>A fresh rule ID. Hyphen-free, so it can never meet a Hyper-V ID in a comparison.</summary>
    public static string NewRuleId() => Guid.NewGuid().ToString("N");

    /// <summary>Hyper-V compares names without regard to case, so a stored name is matched the same way.</summary>
    private static bool NameIs(string? hostName, string? storedName) =>
        !string.IsNullOrWhiteSpace(storedName)
        && string.Equals(hostName?.Trim(), storedName.Trim(), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Where, ISwitchTarget Target)> Targets(AppConfig config)
    {
        foreach (var rule in config.Rules)
            yield return ($"Rule '{rule.Name}'", rule);
        yield return ("Fallback", config.Fallback);
    }

    /// <summary>A deep copy of everything the migration may change; the rest is shared, since nothing
    /// here writes it.</summary>
    private static AppConfig Copy(AppConfig c) => new()
    {
        VirtualMachines = [.. c.VirtualMachines.Select(v => new VmTarget
        {
            Id                       = v.Id,
            Name                     = v.Name,
            NicId                    = v.NicId,
            NicName                  = v.NicName,
            OnBridgeLostAction       = v.OnBridgeLostAction,
            OnBridgeLostDelaySeconds = v.OnBridgeLostDelaySeconds,
        })],
        Rules    = [.. c.Rules.Select(CopyRule)],
        Fallback = new FallbackAction
        {
            SwitchId            = c.Fallback.SwitchId,
            SwitchName          = c.Fallback.SwitchName,
            LegacyVirtualSwitch = c.Fallback.LegacyVirtualSwitch,
            TargetVmIds         = [.. c.Fallback.TargetVmIds],
            LegacyTargetVms     = c.Fallback.LegacyTargetVms is null ? null : [.. c.Fallback.LegacyTargetVms],
        },
        AdapterNames         = c.AdapterNames,
        LogLevel             = c.LogLevel,
        Mqtt                 = c.Mqtt,
        SettingsWindowX      = c.SettingsWindowX,
        SettingsWindowY      = c.SettingsWindowY,
        SettingsWindowWidth  = c.SettingsWindowWidth,
        SettingsWindowHeight = c.SettingsWindowHeight,
    };

    private static NetworkRule CopyRule(NetworkRule r) => new()
    {
        Id                      = r.Id,
        IdGeneratedOnLoad       = r.IdGeneratedOnLoad,
        Name                    = r.Name,
        Priority                = r.Priority,
        Conditions              = new RuleConditions { AdapterMac = r.Conditions?.AdapterMac, IpCidr = r.Conditions?.IpCidr },
        SwitchId                = r.SwitchId,
        SwitchName              = r.SwitchName,
        LegacyVirtualSwitch     = r.LegacyVirtualSwitch,
        TargetVmIds             = [.. r.TargetVmIds],
        LegacyTargetVms         = r.LegacyTargetVms is null ? null : [.. r.LegacyTargetVms],
        AutoStart               = r.AutoStart,
        VmManagementService     = r.VmManagementService,
        HostComputeService      = r.HostComputeService,
        ServiceStopDelaySeconds = r.ServiceStopDelaySeconds,
    };
}
