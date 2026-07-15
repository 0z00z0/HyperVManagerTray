using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Pure state → UI decisions for the VM power controls (issue #30): shape classification (finding 3),
/// allowed-verbs filtering (finding 2), and overlay-expiry deadlines (findings 1 &amp; 7).
/// </summary>
public class VmStateUiTests
{
    // ── ClassifyShape (finding 3) ───────────────────────────────────────────────

    [Theory]
    [InlineData("Running", VmStateUi.Shape.Running)]
    [InlineData("Paused",  VmStateUi.Shape.Paused)]
    [InlineData("Saved",   VmStateUi.Shape.Saved)]
    [InlineData("Off",     VmStateUi.Shape.Off)]
    [InlineData("Starting",     VmStateUi.Shape.Transition)]
    [InlineData("Stopping",     VmStateUi.Shape.Transition)]
    [InlineData("Saving",       VmStateUi.Shape.Transition)]
    [InlineData("Pausing",      VmStateUi.Shape.Transition)]
    [InlineData("Resuming",     VmStateUi.Shape.Transition)]
    [InlineData("Snapshotting", VmStateUi.Shape.Transition)]
    [InlineData("Unknown", VmStateUi.Shape.None)]
    [InlineData("",        VmStateUi.Shape.None)]
    [InlineData(null,      VmStateUi.Shape.None)]
    public void ClassifyShape_MapsStateNames(string? state, VmStateUi.Shape expected)
        => Assert.Equal(expected, VmStateUi.ClassifyShape(state));

    [Fact]
    public void ClassifyShape_IsCaseInsensitive()
        => Assert.Equal(VmStateUi.Shape.Running, VmStateUi.ClassifyShape("running"));

    // Every transient EnabledState name WmiVmMapper.MapState can emit must classify as a transition, so
    // the dashboard renders no power buttons for it (guards against a MapState verb being missed here).
    [Theory]
    [InlineData((ushort)32770)]   // Starting
    [InlineData((ushort)32771)]   // Snapshotting
    [InlineData((ushort)32773)]   // Saving
    [InlineData((ushort)32774)]   // Stopping
    [InlineData((ushort)32776)]   // Pausing
    [InlineData((ushort)32777)]   // Resuming
    public void ClassifyShape_AllMapStateTransientVerbs_AreTransitions(ushort enabledState)
        => Assert.Equal(VmStateUi.Shape.Transition, VmStateUi.ClassifyShape(WmiVmMapper.MapState(enabledState)));

    // ── AllowedVerbs (finding 2) ────────────────────────────────────────────────

    [Fact]
    public void AllowedVerbs_Running_OffersShutdownPauseSave()
        => Assert.Equal(new[] { VmOpKind.Shutdown, VmOpKind.Pause, VmOpKind.Save },
                        VmStateUi.AllowedVerbs("Running"));

    [Fact]
    public void AllowedVerbs_Paused_OffersResumeSave()
        => Assert.Equal(new[] { VmOpKind.Resume, VmOpKind.Save }, VmStateUi.AllowedVerbs("Paused"));

    [Theory]
    [InlineData("Off")]
    [InlineData("Saved")]
    public void AllowedVerbs_StoppedOrSaved_OffersStartOnly(string state)
        => Assert.Equal(new[] { VmOpKind.Start }, VmStateUi.AllowedVerbs(state));

    [Theory]
    [InlineData("Saving")]      // transition
    [InlineData("Starting")]    // transition
    [InlineData("Unknown")]     // none
    [InlineData(null)]          // none
    public void AllowedVerbs_TransitionOrUnknown_OffersNothing(string? state)
        => Assert.Empty(VmStateUi.AllowedVerbs(state));

    // A stopped VM must NOT offer Shutdown (the exact silent-failure the audit flagged), and a running
    // VM must NOT offer Start.
    [Fact]
    public void AllowedVerbs_Off_DoesNotOfferShutdown()
        => Assert.DoesNotContain(VmOpKind.Shutdown, VmStateUi.AllowedVerbs("Off"));

    [Fact]
    public void AllowedVerbs_Running_DoesNotOfferStart()
        => Assert.DoesNotContain(VmOpKind.Start, VmStateUi.AllowedVerbs("Running"));

    // ── CanConnect (cleanup 9) ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Running", true)]
    [InlineData("Paused",  false)]
    [InlineData("Saved",   false)]
    [InlineData("Off",     false)]
    [InlineData("Starting", false)]   // transition
    [InlineData("Unknown", false)]
    [InlineData(null,      false)]
    public void CanConnect_OnlyRunning(string? state, bool expected)
        => Assert.Equal(expected, VmStateUi.CanConnect(state));

    // ── IsOverlayExpired (findings 1 & 7) ───────────────────────────────────────

    [Fact]
    public void IsOverlayExpired_ShutdownRunning_ExpiresAfterDeadline()
    {
        // Just under the deadline → still active; at/after → expired (finding 1: stuck Shutdown). The
        // deadline is deliberately long (a guest can legitimately stay Running for many minutes during
        // an install-on-shutdown), so a value minutes in still must NOT expire.
        Assert.False(VmStateUi.IsOverlayExpired(VmOpKind.Shutdown, VmOpPhase.Running, VmStateUi.ShutdownDeadline - TimeSpan.FromSeconds(1)));
        Assert.False(VmStateUi.IsOverlayExpired(VmOpKind.Shutdown, VmOpPhase.Running, TimeSpan.FromMinutes(5)));
        Assert.True(VmStateUi.IsOverlayExpired(VmOpKind.Shutdown, VmOpPhase.Running, VmStateUi.ShutdownDeadline));
        Assert.True(VmStateUi.IsOverlayExpired(VmOpKind.Shutdown, VmOpPhase.Running, VmStateUi.ShutdownDeadline + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void IsOverlayExpired_NonShutdownRunning_NeverExpiresByAge()
    {
        // Start/Save/etc. reach their target state and are cleared that way — never aged out as Running.
        Assert.False(VmStateUi.IsOverlayExpired(VmOpKind.Start, VmOpPhase.Running, TimeSpan.FromMinutes(10)));
        Assert.False(VmStateUi.IsOverlayExpired(VmOpKind.Save,  VmOpPhase.Running, TimeSpan.FromMinutes(10)));
    }

    [Theory]
    [InlineData(VmOpKind.Start)]
    [InlineData(VmOpKind.Shutdown)]
    [InlineData(VmOpKind.Pause)]
    public void IsOverlayExpired_Failed_ExpiresAfterLifetime_ForAnyKind(VmOpKind kind)
    {
        Assert.False(VmStateUi.IsOverlayExpired(kind, VmOpPhase.Failed, TimeSpan.FromSeconds(44)));
        Assert.True(VmStateUi.IsOverlayExpired(kind, VmOpPhase.Failed, VmStateUi.FailedOverlayLifetime));
    }

    [Fact]
    public void IsOverlayExpired_RequestedPhase_NeverExpires()
        => Assert.False(VmStateUi.IsOverlayExpired(VmOpKind.Start, VmOpPhase.Requested, TimeSpan.FromHours(1)));
}
