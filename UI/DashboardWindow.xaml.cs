using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

/// <summary>
/// Borderless Mica popup: host-network status card on top, then a control card per configured
/// VM (current switch/rule, state, CPU / memory / VHD meters, power buttons).  Appears
/// bottom-right above the taskbar and auto-dismisses when it loses focus.  VM status/metrics come
/// from <see cref="VmService"/> (WMI event watcher + a metrics loop) only while this window is
/// subscribed (<see cref="SubscribeMetricsIfNeeded"/>/<see cref="UnsubscribeMetricsIfNeeded"/>),
/// so a closed dashboard costs nothing.
/// </summary>
public sealed partial class DashboardWindow : Window
{
    private const double ContentWidth = 320;
    private const int    EdgeMargin   = 12;

    // Cold boot under host load can legitimately take a while; long enough to cover that without
    // hanging the "Start & Connect" button indefinitely if the VM is unusually slow to report in.
    private static readonly TimeSpan StartAndConnectTimeout = TimeSpan.FromSeconds(45);

    private readonly ConfigManager  _config;
    private readonly NetworkMonitor _monitor;
    private readonly HyperVManager  _hyperV;   // Connect-VM-NIC / switch (PowerShell, Phase 1)
    private readonly VmService      _vm;       // status/metrics/power/IPs (WMI, event-driven)

    private IReadOnlyList<VmStatus> _latest = [];
    // In-flight/failed power-op message per VM, overlaid on the card's state label (optimistic
    // "Requesting start…", live "Saving (47%)…", or a sticky "Failed: not enough memory").
    private readonly Dictionary<string, VmOperationProgress> _op = new(StringComparer.OrdinalIgnoreCase);
    // When each VM's current _op entry was last (re)set, so an overlay that never clears on its own can
    // be aged out: a hung/cancelled graceful Shutdown stuck in the Running phase (issue #30, finding 1),
    // and a sticky "Failed: …" overlay (finding 7). See VmStateUi.IsOverlayExpired.
    private readonly Dictionary<string, DateTime> _opSince = new(StringComparer.OrdinalIgnoreCase);
    // Per-VM display state that bridges the gaps in the live WMI read. Each entry is (Name, HoldUntil):
    //   • HoldUntil in the FUTURE → an op just succeeded and we HOLD Name (its achieved state) until the
    //     lagging EnabledState catches up or the short grace expires, so a stale pre-op read can't flip
    //     the label back (e.g. a completed Pause briefly showing "Running"). A diverged reality (a
    //     shutdown cancelled in the guest) wins once the grace lapses. Makes the card jump straight to
    //     the achieved state on success.
    //   • HoldUntil in the PAST → Name is just the last state we could actually name, shown as a
    //     fallback while EnabledState is briefly unpopulated/"Unknown" after an op settles (the window
    //     where MMC's own State/Status columns also blank), so the label never goes blank.
    private readonly Dictionary<string, (string Name, DateTime HoldUntil)> _effectiveState = new(StringComparer.OrdinalIgnoreCase);
    private bool _metricsOn;   // true while subscribed to VmService metrics (dashboard shown)
    private bool _priming;     // true during the off-screen composition warm-up (see Prime)

    /// <summary>
    /// When false (the default), a close request (Alt+F4, etc.) is cancelled and the window is
    /// hidden instead of destroyed — so the primed, already-composed window is reused on the next
    /// open and never shows the Mica white-flash.  App sets this true at shutdown so the app can
    /// actually exit.
    /// </summary>
    public bool AllowClose { get; set; }

    // ── Same-click detection ────────────────────────────────────────────────────
    // Clicking the tray icon while the popup is open first deactivates it (auto-hide),
    // then delivers the click command — without a guard that click would instantly
    // re-show the window it just toggled closed.  Time alone can't tell that apart from
    // a genuine quick re-open (dismiss by clicking the desktop, then click the tray), so
    // we also require the cursor to still be where it was when the hide happened: for
    // the click-through case both events come from the same physical click (distance≈0);
    // for a real re-open the cursor has travelled from the dismiss point to the tray.
    private const int SameClickMs       = 400;
    private const int SameClickRadiusPx = 24;

    private DateTime  _hiddenAtUtc = DateTime.MinValue;
    private (int X, int Y) _hiddenCursor = (int.MinValue, int.MinValue);

    /// <summary>True when the tray click being handled is the same physical click that just auto-hid this window.</summary>
    public bool HiddenByThisClick
    {
        get
        {
            if ((DateTime.UtcNow - _hiddenAtUtc).TotalMilliseconds > SameClickMs) return false;
            var (x, y) = NativeMethods.GetCursorPosition();
            return Math.Abs(x - _hiddenCursor.X) <= SameClickRadiusPx
                && Math.Abs(y - _hiddenCursor.Y) <= SameClickRadiusPx;
        }
    }

    public DashboardWindow(ConfigManager config, NetworkMonitor monitor, HyperVManager hyperV, VmService vm)
    {
        _config  = config;
        _monitor = monitor;
        _hyperV  = hyperV;
        _vm      = vm;

        InitializeComponent();
        TitleText.Text = AppInfo.Name;   // issue #42 — never the MMC snap-in's name
        ConfigureWindowChrome();

        _vm.StatusesChanged   += OnVmStatusesChanged;
        _vm.OperationProgress += OnVmOperationProgress;

        Activated       += OnActivated;
        AppWindow.Closing += OnAppWindowClosing;
        Closed    += (_, _) =>
        {
            UnsubscribeMetricsIfNeeded();
            _vm.StatusesChanged   -= OnVmStatusesChanged;
            _vm.OperationProgress -= OnVmOperationProgress;
        };
    }

    /// <summary>Hides (rather than destroys) the popup on a close request, unless <see cref="AllowClose"/>
    /// is set — keeps the primed window alive so reopening never white-flashes.</summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (AllowClose) return;
        args.Cancel = true;
        HideWindow();
    }

    // ── Public surface ──────────────────────────────────────────────────────────

    public void ShowNearTray()
    {
        UiActivityLog.Logger.LogInformation("Window: Dashboard shown");
        Refresh();
        ResizeAndPlace();
        AppWindow.Show();
        Activate();
    }

    public void HideWindow()
    {
        UiActivityLog.Logger.LogInformation("Window: Dashboard hidden");
        UnsubscribeMetricsIfNeeded();
        _hiddenAtUtc  = DateTime.UtcNow;
        _hiddenCursor = NativeMethods.GetCursorPosition();
        AppWindow.Hide();
    }

    private void SubscribeMetricsIfNeeded()
    {
        if (_metricsOn) return;
        _metricsOn = true;
        _vm.SubscribeMetrics();
    }

    private void UnsubscribeMetricsIfNeeded()
    {
        if (!_metricsOn) return;
        _metricsOn = false;
        _vm.UnsubscribeMetrics();
    }

    /// <summary>
    /// One-time, off-screen composition warm-up to eliminate the white flash on first open.
    /// A Mica window paints its HWND white for the first frame before the backdrop composes;
    /// doing that first frame here (tiny and far off-screen, so it's invisible) means the first
    /// real <see cref="ShowNearTray"/> reuses an already-composed window and never flashes.
    /// Call once, on the UI thread, at startup.
    /// </summary>
    public void Prime()
    {
        _priming = true;   // suppress OnActivated's timer/load + auto-hide during the warm-up
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        AppWindow.Show();
        // Hide after a frame has composed (low priority runs post-render), then arm for real use.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            AppWindow.Hide();
            _priming = false;
        });
    }

    /// <summary>Called by App (UI thread) when a switch change is applied.</summary>
    public void OnSwitchApplied(MatchResult result) => ApplyHostStatus(result);

    // ── Window chrome / placement ───────────────────────────────────────────────

    private void ConfigureWindowChrome()
    {
        AppWindow.IsShownInSwitchers = false;
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
    }

    private void ResizeAndPlace()
    {
        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();

        Root.Width = ContentWidth;
        Root.Measure(new Size(ContentWidth, double.PositiveInfinity));
        double contentHeight = Root.DesiredSize.Height;

        int w      = (int)Math.Ceiling(ContentWidth * scale);
        int margin = (int)Math.Ceiling(EdgeMargin   * scale);
        int maxH   = work.Bottom - work.Top - margin * 2;
        int h      = Math.Min((int)Math.Ceiling(contentHeight * scale), maxH);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        AppWindow.Move(new Windows.Graphics.PointInt32(work.Right - w - margin, work.Bottom - h - margin));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_priming) return;   // ignore activation churn during the off-screen warm-up

        if (e.WindowActivationState == WindowActivationState.Deactivated)
            HideWindow();
        else
            SubscribeMetricsIfNeeded();   // triggers an immediate refresh (see VmService.SubscribeMetrics)
    }

    // ── Host network card ───────────────────────────────────────────────────────

    private void Refresh()
    {
        // Never evaluate the network synchronously here — GetAllNetworkInterfaces() +
        // GetIPProperties() can block the UI thread for hundreds of ms, delaying the
        // window's appearance.  LastApplied is populated by NetworkMonitor's immediate
        // startup evaluation; until it lands, the host card keeps its placeholder text
        // and OnSwitchApplied fills it in moments later.
        if (_monitor.LastApplied is { } result) ApplyHostStatus(result);

        // First open only: build placeholder "Updating" cards so the popup opens at full
        // size instead of growing when data arrives.  On later opens the cards from the
        // previous session are still present (the window is hidden, never closed) and
        // OnActivated's SubscribeMetricsIfNeeded triggers an immediate VmService refresh
        // that repopulates them via OnVmStatusesChanged.
        if (VmPanel.Children.Count == 0) BuildCards([]);
    }

    /// <summary>
    /// Fills the HOST NETWORK card. The Rule row reports the apply OUTCOME, not just the matched rule
    /// (issue #37): a failed bind/reconnect renders as e.g. "Office LAN — bind failed" in red, the same
    /// way a VM card overlays "Failed: …". Before #37 this card showed the rule's intent unconditionally,
    /// so it claimed the bridged rule was in force while the VM was still on the old switch.
    /// </summary>
    private void ApplyHostStatus(MatchResult result)
    {
        RuleText.Text = NetworkStatusUi.RuleRowText(result.RuleName, result.ApplyStatus);

        // Red on failure; on recovery CLEAR the local value rather than re-assigning a captured brush,
        // so the row falls back to RowValueStyle's {ThemeResource TextFillColorPrimaryBrush} and keeps
        // tracking light/dark. Assigning a captured brush would pin a hard local value and freeze this
        // one row's colour across a theme switch.
        if (NetworkStatusUi.IsFailure(result.ApplyStatus))
            RuleText.Foreground = AppColors.IndicatorRedBrush;
        else
            RuleText.ClearValue(TextBlock.ForegroundProperty);

        AdapterText.Text = result.HostAdapterName;
        IpText.Text      = result.HostIp;
        GatewayText.Text = result.Gateway;
        DnsText.Text     = result.DnsServers.Count > 0
            ? string.Join("  ·  ", result.DnsServers.Take(2)) : "—";
    }

    // ── Per-VM cards ────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised by <see cref="VmService"/> (background thread) on the state-change watcher, the
    /// metrics timer, or right after <see cref="VmService.SubscribeMetrics"/>. Replaces the old
    /// 1-second UI-thread poll: cards now update the instant WMI reports something changed.
    /// </summary>
    private void OnVmStatusesChanged(IReadOnlyList<VmStatus> statuses)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _latest = statuses;
            bool layoutChanged = BuildCards(statuses);
            // Only re-measure the window when a card's layout actually changed (rows
            // appeared/disappeared); pure value updates never affect the size.  Defer
            // by one frame so WinUI's layout pass has processed the new children —
            // otherwise DesiredSize still reflects the previous layout.
            if (layoutChanged)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (AppWindow.IsVisible) ResizeAndPlace();
                });
        });
    }

    /// <summary>
    /// Raised by <see cref="VmService"/> as a power action progresses: an optimistic "Requesting
    /// start…" the instant a button is clicked, then live WMI-job percent, then success (state
    /// watcher takes over) or the exact failure text (e.g. "not enough memory").
    /// </summary>
    private void OnVmOperationProgress(VmOperationProgress progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (progress.Phase == VmOpPhase.Succeeded)
            {
                RemoveOp(progress.VmName);
                // Show the achieved state immediately: the real EnabledState can lag several seconds
                // behind a completed op (blank/"Unknown", or still the pre-op state, in the meantime).
                // Hold the achieved state (future HoldUntil) so EffectiveStateName won't let a stale
                // live read overwrite it before the WMI read catches up.
                if (SettledState(progress.Kind) is { } settled)
                    _effectiveState[progress.VmName] = (settled, DateTime.UtcNow.AddSeconds(6));
            }
            else
            {
                _op[progress.VmName]      = progress;
                _opSince[progress.VmName] = DateTime.UtcNow;   // (re)start the overlay's age clock
            }

            // Update just the affected card in place — no need to wait for the next status tick.
            if (_cards.TryGetValue(progress.VmName, out var card))
                ApplyOverlay(card, FindStatus(_latest, progress.VmName));
        });
    }

    // ── Card cache ──────────────────────────────────────────────────────────────
    // Cards are rebuilt only when their layout shape changes (state category, meter rows,
    // button set); per-tick value changes (CPU %, memory, uptime) are written into the
    // existing TextBlocks/ProgressBars.  This avoids reconstructing ~40 UI objects per VM
    // every second while the dashboard is open.

    private sealed class VmCard
    {
        public required Border      Root;
        public required string      VmName;        // config VM name — keys the IP/op-progress lookups
        public required string      Shape;         // layout signature — rebuild when it changes
        public required TextBlock   State;
        public required TextBlock   Subtitle;
        public required TextBlock   Ip;            // right-justified IPv4 on the subtitle line
        public required StackPanel  ButtonsPanel;   // power buttons — disabled while an op is in flight
        public TextBlock?   Uptime;
        public TextBlock?   CpuValue;
        public ProgressBar? CpuBar;
        public TextBlock?   MemValue;
        public ProgressBar? MemBar;
        public TextBlock?   DiskValue;
    }

    private readonly Dictionary<string, VmCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _cardOrder = [];

    /// <summary>The "no VMs yet" card (issue #38), or null when real VM cards are showing. Tracked so
    /// the empty state is built once and removed the moment the first VM lands in config.</summary>
    private Border? _emptyState;

    /// <summary>Categorises everything that affects a card's row/button layout. Uses the shared
    /// <see cref="VmStateUi.ClassifyShape"/> so a transitional state gets its own shape (no power
    /// buttons) and rebuilds the card when the transition lands (issue #30, finding 3).</summary>
    private static string ShapeOf(VmStatus? s) =>
        VmStateUi.ClassifyShape(s?.State).ToString()
        + "|" + (FormatUptime(s).Length > 0)
        + "|" + (s is { VhdBytes: > 0 });

    /// <summary>
    /// Creates/updates the VM cards; returns true when any card's layout changed
    /// (so the window needs re-measuring).
    /// </summary>
    private bool BuildCards(IReadOnlyList<VmStatus> statuses)
    {
        var vms = _config.Current.VirtualMachines;

        // Zero managed VMs → an empty-state card (issue #38). Previously this method simply added no
        // children, leaving the "VIRTUAL MACHINES" header floating over blank space — which reads as a
        // broken window, not as "you haven't set anything up yet", and points at nothing. Note this
        // MUST be handled before the SequenceEqual check below: on a fresh install both `vms` and
        // `_cardOrder` are empty, so that check passes, the loop iterates nothing, and no card would
        // ever be added.
        if (vms.Count == 0)
        {
            if (_emptyState is not null) return false;   // already showing it
            VmPanel.Children.Clear();
            _cards.Clear();
            _cardOrder = [];
            _emptyState = BuildEmptyStateCard();
            VmPanel.Children.Add(_emptyState);
            return true;
        }

        // First VM added (config hot-reload) → drop the empty state and fall through to a real rebuild
        // (_cardOrder is empty, so the wholesale branch below fires).
        if (_emptyState is not null)
        {
            VmPanel.Children.Remove(_emptyState);
            _emptyState = null;
        }

        // VM set/order changed (config edit, first open) → rebuild the panel wholesale.
        if (!vms.Select(v => v.Name).SequenceEqual(_cardOrder, StringComparer.OrdinalIgnoreCase))
        {
            VmPanel.Children.Clear();
            _cards.Clear();
            _cardOrder = vms.Select(v => v.Name).ToList();
            foreach (var vm in vms)
            {
                var card = BuildCard(vm, FindStatus(statuses, vm.Name));
                _cards[vm.Name] = card;
                VmPanel.Children.Add(card.Root);
            }
            return true;
        }

        bool layoutChanged = false;
        for (int i = 0; i < vms.Count; i++)
        {
            var vm = vms[i];
            var s  = FindStatus(statuses, vm.Name);
            if (!_cards.TryGetValue(vm.Name, out var card) || card.Shape != ShapeOf(s))
            {
                card = BuildCard(vm, s);          // layout shape changed → rebuild this card
                _cards[vm.Name]      = card;
                VmPanel.Children[i]  = card.Root;
                layoutChanged        = true;
            }
            else
            {
                UpdateCard(card, s);              // same shape → update values in place
            }
        }
        return layoutChanged;
    }

    private static VmStatus? FindStatus(IReadOnlyList<VmStatus> statuses, string name) =>
        statuses.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void UpdateCard(VmCard card, VmStatus? s)
    {
        ApplyOverlay(card, s);
        card.Subtitle.Text = Subtitle(s);
        card.Ip.Text        = _vm.GetCachedVmIp(card.VmName) ?? "";
        if (card.Uptime is not null) card.Uptime.Text = FormatUptime(s);
        if (s is null) return;
        if (card.CpuValue is not null) { card.CpuValue.Text = $"{s.Cpu}%"; SetBar(card.CpuBar, s.Cpu / 100.0); }
        if (card.MemValue is not null) { card.MemValue.Text = $"{s.MemAssignedMb:N0} MB"; SetBar(card.MemBar, s.MemoryFraction); }
        if (card.DiskValue is not null) card.DiskValue.Text = $"{s.VhdGb:N1} GB";
    }

    // ── Live power-op overlay ────────────────────────────────────────────────────
    // A VM power action doesn't wait for a WMI round-trip before the UI reacts: BeginPowerAction
    // raises an optimistic "Requesting start…" synchronously, then live job percent, then success
    // (the state watcher takes over) or the exact failure text. This overlays that message onto the
    // state label and disables the card's buttons while the operation is in flight, applied both
    // per-tick (UpdateCard) and the instant a progress event arrives (OnVmOperationProgress).

    private void ApplyOverlay(VmCard card, VmStatus? s)
    {
        // An overlay can outlive its operation and needs the real, state-watcher-driven status to
        // retire it once the VM reaches the operation's target state:
        //   • a "Failed" that was really a false "timed out" (TrackJob's poll deadline elapsed while a
        //     slow job was still running), which nothing else clears; and
        //   • a graceful Shutdown, which only ever emits a "Shutting down…" Running phase (the guest
        //     powers off asynchronously) and so never gets a Succeeded to clear it.
        // Succeeded ops are already removed in OnVmOperationProgress, so this covers the stuck cases.
        if (_op.TryGetValue(card.VmName, out var staleOp) && ReachedTarget(staleOp.Kind, s))
            RemoveOp(card.VmName);

        // Age-out overlays that never clear on their own: a hung/cancelled graceful Shutdown that stays
        // in the Running phase (finding 1), and a sticky "Failed: …" overlay (finding 7). Runs on every
        // ~2.5 s metrics tick while the dashboard is open, so a stuck card re-enables itself.
        if (_op.TryGetValue(card.VmName, out var timedOp) &&
            _opSince.TryGetValue(card.VmName, out var since) &&
            VmStateUi.IsOverlayExpired(timedOp.Kind, timedOp.Phase, DateTime.UtcNow - since))
            RemoveOp(card.VmName);

        if (_op.TryGetValue(card.VmName, out var op) && op.Message is { Length: > 0 } msg)
        {
            card.State.Text       = Truncate(msg, 30);
            card.State.Foreground = op.Phase == VmOpPhase.Failed ? AppColors.IndicatorRedBrush : AppColors.IndicatorOrangeBrush;
            // The status text is truncated to keep the card compact (e.g. a WMI failure message can
            // run far longer than the "Failed: 'name' failed to…" that fits) — surface the full
            // message on hover so it isn't lost. Only set when actually truncated, so a short message
            // doesn't get a redundant tooltip.
            ToolTipService.SetToolTip(card.State, msg.Length > 30 ? msg : null);
        }
        else
        {
            var state = EffectiveStateName(card.VmName, s);
            // The VM's active Msvm_ConcreteJob carries a finer-grained verb+percent than the coarse
            // EnabledState-derived state — e.g. "Restoring (10%)" for a resume-from-Saved that reports
            // the same EnabledState ("Starting") as a cold boot (issue #13). This mirrors Hyper-V
            // Manager's Status column and covers operations started externally in MMC, not just
            // app-initiated ones. Prefer it for the displayed text when present, but keep colouring on
            // the coarse `state` — BrushForState only recognises a small fixed set of names
            // ("Running"/"Paused"/"Saved"), and an unrecognised verb like "Restoring" would otherwise
            // fall through to the grey catch-all instead of "Starting"'s usual colour.
            card.State.Text       = s is { JobStatus: { Length: > 0 } job } ? job : state;
            card.State.Foreground = BrushForState(state);
            ToolTipService.SetToolTip(card.State, null);   // clear any tooltip left by a prior overlay message
        }

        SetButtonsEnabled(card, !IsOpActive(card.VmName));
    }

    /// <summary>
    /// The state string to show for a VM — never empty and never the bare "Unknown" sentinel MapState
    /// emits for an unpopulated/unmapped EnabledState. A recognised state is remembered as the
    /// last-known-good; when the live read is null or "Unknown" (the post-operation settling window,
    /// where MMC's own State/Status columns also go blank) the remembered value is shown instead, or
    /// "Updating" if we've never yet seen a good state for this VM.
    /// </summary>
    private string EffectiveStateName(string vmName, VmStatus? s)
    {
        var live = s?.State is { Length: > 0 } st && !st.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? st : null;
        bool remembered = _effectiveState.TryGetValue(vmName, out var entry);

        // A just-succeeded op holds its achieved state (HoldUntil in the future): keep showing it so a
        // stale pre-op read can't flip the label back — until the live read confirms it (they agree) or
        // the grace lapses.
        if (remembered && entry.HoldUntil > DateTime.UtcNow
            && !string.Equals(live, entry.Name, StringComparison.OrdinalIgnoreCase))
            return entry.Name;

        // Otherwise a recognised live read wins and becomes the fallback (past HoldUntil); an
        // unrecognised one ("Unknown"/blank in the post-op settling window) falls back to what we last
        // named, or "Updating" if we've never seen a good state for this VM.
        if (live is not null)
        {
            _effectiveState[vmName] = (live, DateTime.MinValue);
            return live;
        }
        return remembered ? entry.Name : "Updating";
    }

    private static Brush BrushForState(string state) =>
        state.Equals("Running", StringComparison.OrdinalIgnoreCase) ? AppColors.IndicatorGreenBrush
      : state.Equals("Paused",  StringComparison.OrdinalIgnoreCase)
        || state.Equals("Saved", StringComparison.OrdinalIgnoreCase) ? AppColors.IndicatorOrangeBrush
      :                                                                 AppColors.IndicatorGreyBrush;

    /// <summary>
    /// The steady state a completed power action leaves the VM in — used both to clear an overlay once
    /// the real status catches up (a stale "timed out" Failed, or Shutdown, which completes
    /// asynchronously via the guest and only ever emits a "Shutting down…" Running phase, never
    /// Succeeded), and to show the achieved state instantly on success while the WMI read lags.
    /// </summary>
    private static string? SettledState(VmOpKind kind) => kind switch
    {
        VmOpKind.Start or VmOpKind.Resume => "Running",
        VmOpKind.Pause                     => "Paused",
        VmOpKind.Save                      => "Saved",
        VmOpKind.Shutdown                  => "Off",
        _                                  => null,
    };

    private static bool ReachedTarget(VmOpKind kind, VmStatus? s) =>
        SettledState(kind) is { } target && s?.State.Equals(target, StringComparison.OrdinalIgnoreCase) == true;

    private bool IsOpActive(string vmName) =>
        _op.TryGetValue(vmName, out var op) && op.Phase is VmOpPhase.Requested or VmOpPhase.Running;

    /// <summary>Removes a VM's overlay op and its age timestamp together, keeping the two dictionaries in sync.</summary>
    private void RemoveOp(string vmName)
    {
        _op.Remove(vmName);
        _opSince.Remove(vmName);
    }

    private static void SetButtonsEnabled(VmCard card, bool enabled)
    {
        foreach (var child in card.ButtonsPanel.Children)
            if (child is Button b) b.IsEnabled = enabled;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void SetBar(ProgressBar? bar, double fraction)
    {
        if (bar is null) return;
        bar.Value      = Math.Clamp(fraction * 100, 0, 100);
        bar.Foreground = BarBrush(fraction);
    }

    private static Brush BarBrush(double fraction) =>
        fraction <= 0.5  ? AppColors.GaugeLowBrush
      : fraction <= 0.85 ? AppColors.GaugeMedBrush
      :                    AppColors.GaugeHighBrush;

    private string Subtitle(VmStatus? s)
    {
        var switchText = !string.IsNullOrWhiteSpace(s?.Switch) ? s!.Switch : "—";
        return $"{switchText}  ·  {_monitor.LastApplied?.RuleName ?? "—"}";
    }

    /// <summary>
    /// The zero-VMs card (issue #38). Same Border idiom as a real VM card so the section still reads as
    /// a populated list rather than a rendering failure, and it names the ONE next action that matters.
    /// Deliberately points at the tray icon rather than a specific menu item — a signpost that names a
    /// menu path is a signpost that goes stale, which issue #34 then promptly demonstrated by replacing
    /// the VM Power menu with "Manage VMs". This card needed no edit for that, which is the point.
    /// </summary>
    private static Border BuildEmptyStateCard() => new()
    {
        CornerRadius    = new CornerRadius(6),
        Padding         = new Thickness(10, 8, 10, 8),
        Background      = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush     = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            // Names the list to use, rather than leaving the user to find it (issue #42) — matching how
            // the Settings and override messages point at the same place.
            Text         = "No VMs are managed yet.\nRight-click the tray icon and use Manage VMs to add one.",
            FontSize     = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity      = 0.75,
        },
    };

    private VmCard BuildCard(VmTarget vm, VmStatus? s)
    {
        bool running = s?.IsRunning == true;
        var rows = new StackPanel { Spacing = 6 };

        // ── Header: VM name + state (+ uptime when running) ─────────────────
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text              = vm.Name,
            FontSize          = 12,
            FontWeight        = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRowSpan(title, 2);
        // Text/Foreground set below via ApplyOverlay once the VmCard exists (single source of
        // truth for the state label, shared with UpdateCard and OnVmOperationProgress).
        var stateLabel = new TextBlock
        {
            FontSize             = 12,   // matches the dashboard's unified right-column value size
            VerticalAlignment    = VerticalAlignment.Center,
            HorizontalAlignment  = HorizontalAlignment.Right,
        };
        Grid.SetColumn(stateLabel, 1);
        Grid.SetRow(stateLabel, 0);
        header.Children.Add(title);
        header.Children.Add(stateLabel);

        TextBlock? uptimeLbl = null;
        var uptimeText = FormatUptime(s);
        if (!string.IsNullOrEmpty(uptimeText))
        {
            uptimeLbl = new TextBlock
            {
                Text                = uptimeText,
                FontSize            = 12,   // matches the dashboard's unified right-column value size
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground          = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            };
            Grid.SetColumn(uptimeLbl, 1);
            Grid.SetRow(uptimeLbl, 1);
            header.Children.Add(uptimeLbl);
        }

        rows.Children.Add(header);

        // ── Switch / rule subtitle (left) + guest IPv4 (right, stands alone) ──
        var tertiary = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        var subRow = new Grid();
        subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var subtitle = new TextBlock
        {
            Text              = Subtitle(s),
            FontSize          = 10,
            Foreground        = tertiary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(subtitle, 0);

        // IP comes from VmService's cache (WMI, refreshed by the metrics loop/tooltip path) — no extra poll.
        var ipLabel = new TextBlock
        {
            Text                = _vm.GetCachedVmIp(vm.Name) ?? "",
            FontSize            = 12,   // matches the dashboard's unified right-column value size
            Foreground          = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(ipLabel, 1);

        subRow.Children.Add(subtitle);
        subRow.Children.Add(ipLabel);
        rows.Children.Add(subRow);

        // ── Metrics (running VMs only) ───────────────────────────────────────
        (TextBlock Value, ProgressBar? Bar)? cpu = null, mem = null, disk = null;
        if (running && s is not null)
        {
            cpu = AddMeter(rows, "CPU", $"{s.Cpu}%",                s.Cpu / 100.0);
            mem = AddMeter(rows, "Mem", $"{s.MemAssignedMb:N0} MB", s.MemoryFraction);
        }
        // "VHD", not "Disk" (issue #42): the value is the summed size of the VM's VHD FILES on the
        // host (VmService.RefreshVhd → FileInfo.Length), not the guest's disk usage or free space.
        // Labelled "Disk" beside CPU/Mem it read as a guest metric, which it is not.
        if (s is not null && s.VhdBytes > 0)
            disk = AddMeter(rows, "VHD", $"{s.VhdGb:N1} GB", -1);

        // ── Power buttons ────────────────────────────────────────────────────
        var buttonsPanel = BuildButtons(vm, s);
        rows.Children.Add(buttonsPanel);

        var root = new Border
        {
            CornerRadius  = new CornerRadius(6),
            Padding       = new Thickness(10, 8, 10, 8),
            Background    = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush   = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child         = rows,
        };

        var card = new VmCard
        {
            Root         = root,
            VmName       = vm.Name,
            Shape        = ShapeOf(s),
            State        = stateLabel,
            Subtitle     = subtitle,
            Ip           = ipLabel,
            ButtonsPanel = buttonsPanel,
            Uptime       = uptimeLbl,
            CpuValue     = cpu?.Value,  CpuBar = cpu?.Bar,
            MemValue     = mem?.Value,  MemBar = mem?.Bar,
            DiskValue    = disk?.Value,
        };
        ApplyOverlay(card, s);
        return card;
    }

    // Adds a "label + optional progress bar + value" row and returns the mutable parts
    // so UpdateCard can refresh them in place on later ticks.
    private static (TextBlock Value, ProgressBar? Bar) AddMeter(
        StackPanel rows, string label, string value, double fraction)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text              = label,
            FontSize          = 11,   // matches the dashboard's larger grey left-column label size
            FontWeight        = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground        = AppColors.IndicatorGreyBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        g.Children.Add(lbl);

        ProgressBar? bar = null;
        if (fraction >= 0)
        {
            bar = new ProgressBar
            {
                Minimum           = 0,
                Maximum           = 100,
                Value             = Math.Clamp(fraction * 100, 0, 100),
                Height            = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground        = BarBrush(fraction),
            };
            Grid.SetColumn(bar, 1);
            g.Children.Add(bar);
        }

        var val = new TextBlock { Text = value, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(val, 2);
        g.Children.Add(val);

        rows.Children.Add(g);
        return (val, bar);
    }

    /// <summary>
    /// Horizontal padding of a card's action buttons. Trimmed 8 → 6 when the brand mono face landed
    /// (issue #44), because this non-wrapping row is the one place the dashboard's fixed 320 DIP width
    /// genuinely binds. The worst case is a Running VM — "Shut down" + "Pause" + "Save" + "Connect":
    ///
    ///   available = 320 - Root padding (20+20) - card padding (10+10) - card border (1+1) = 258 DIP
    ///   captions at 11 px Cascadia Mono (0.586 em/char, measured from the shipped .ttf) = 161.1
    ///   button chrome = (6 + 6 padding + 1 + 1 border) × 4 = 56;  spacing = 6 × 3 gaps = 18
    ///   total = 235.1 DIP  → ~23 DIP of slack
    ///
    /// At the previous padding of 8 the same row measured ~251 of 258 — it still fitted, but on ~7 DIP
    /// of slack, less than a single character, with no margin for the difference between that measure
    /// and DirectWrite's own. The trim restores the ~25 DIP the row had in the default sans (~233 of
    /// 258), i.e. this keeps the row's density where it was rather than making it tighter.
    ///
    /// The font size is deliberately NOT reduced: at 11 px these captions are already small, and
    /// shrinking text is a worse trade than shaving padding. If a future verb set overflows anyway,
    /// the fix is to let the row wrap, not to shrink it further.
    /// </summary>
    private const double CardButtonPaddingX = 6;

    private StackPanel BuildButtons(VmTarget vm, VmStatus? s)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };

        // Power actions are synchronous, non-blocking fire-and-forget calls: BeginPowerAction
        // raises the optimistic "Requesting …" overlay before returning, then reports live
        // progress/failure via VmService.OperationProgress. No await, no reload — the card updates
        // itself from that event and from the state watcher.
        void PowerBtn(string text, VmOpKind kind) => panel.Children.Add(new Button
        {
            Content  = text,
            FontSize = 11,
            Padding  = new Thickness(CardButtonPaddingX, 3, CardButtonPaddingX, 3),
            Command  = new RelayCommand(() =>
            {
                UiActivityLog.Logger.LogInformation("Dashboard: {Command} '{Vm}'", text, vm.Name);
                _vm.BeginPowerAction(vm.Name, kind, VmOpOrigin.Dashboard);
            }),
        });

        // The two actions that also launch vmconnect.exe still need an awaited Task.
        void TaskBtn(string text, Func<Task> action) => panel.Children.Add(new Button
        {
            Content  = text,
            FontSize = 11,
            Padding  = new Thickness(CardButtonPaddingX, 3, CardButtonPaddingX, 3),
            Command  = new RelayCommand(() =>
            {
                UiActivityLog.Logger.LogInformation("Dashboard: {Command} '{Vm}'", text, vm.Name);
                _ = action();
            }),
        });

        // Buttons are driven off the shared VmStateUi verb model (cleanup 9) so the dashboard, the tray
        // VM-power menu and the state classifier can't drift: the power verbs come from AllowedVerbs,
        // "Connect" from CanConnect, and "Start & Connect" is offered exactly when Start is (an Off/Saved
        // VM). A transitional (Saving/Stopping/…) or as-yet-unknown state yields no verbs — offering any
        // mid-transition would just return 0x8007 — and the state watcher rebuilds the card the instant
        // the transition lands (finding 3).
        var allowed = VmStateUi.AllowedVerbs(s?.State);
        foreach (var kind in allowed)
            PowerBtn(PowerVerbLabel(kind), kind);
        if (allowed.Contains(VmOpKind.Start))
            TaskBtn("Start & Connect", () => StartAndConnectAsync(vm));
        if (VmStateUi.CanConnect(s?.State))
            TaskBtn("Connect", () => ConnectAsync(vm));
        return panel;
    }

    /// <summary>Button caption for a power verb on the dashboard card (the tray menu uses "&amp;&amp;"-escaped
    /// captions for its composite items, so the label lives at each call site rather than in VmStateUi).</summary>
    private static string PowerVerbLabel(VmOpKind kind) => kind switch
    {
        VmOpKind.Start    => "Start",
        // "Shut down", not "Shutdown": it is the verb the user invokes, and the Settings bridge-lost
        // picker has always said "Shut down" (SettingsOptions.BridgeLostActions) — the app disagreed
        // with itself about its own vocabulary (issue #42).
        VmOpKind.Shutdown => "Shut down",
        VmOpKind.Pause    => "Pause",
        VmOpKind.Save     => "Save",
        VmOpKind.Resume   => "Resume",
        _                 => kind.ToString(),
    };

    private async Task ConnectAsync(VmTarget vm)
    {
        var sw = _monitor.LastApplied?.VirtualSwitch;
        if (!string.IsNullOrEmpty(sw)) await _hyperV.ApplySwitchAsync(vm.Name, vm.NicName, sw);
        Shell.OpenVmConnect(vm.Name);
    }

    private async Task StartAndConnectAsync(VmTarget vm)
    {
        // BeginPowerAction is fire-and-forget (see PowerBtn); WaitUntilRunningAsync replaces the old
        // flat 2.5s guess with an actual readiness wait (event-driven off VmService.StatusesChanged —
        // see its doc comment). On timeout it proceeds anyway rather than hanging the button — vmconnect
        // itself tolerates attaching to a VM that's still finishing boot.
        _vm.BeginPowerAction(vm.Name, VmOpKind.Start, VmOpOrigin.Dashboard);
        var readiness = await _vm.WaitUntilRunningAsync(vm.Name, StartAndConnectTimeout);
        // Don't launch vmconnect onto a VM that failed to start (issue #30, finding 6); a plain timeout
        // still connects, since vmconnect tolerates attaching to a VM that's still finishing boot.
        if (readiness == VmService.StartReadiness.Failed) return;
        await ConnectAsync(vm);
    }

    /// <summary>
    /// Formats the VM uptime for display on the card header.
    /// Returns empty string when the VM is not running or the uptime string is unavailable.
    /// Examples: "47m", "3h 14m", "1d 3h".
    /// </summary>
    private static string FormatUptime(VmStatus? s) => UptimeFormatter.Format(s);
}
