using System.Text.RegularExpressions;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The two properties a failure line must hold, both of which fail silently otherwise: it fits the
/// card's state column beside its dismiss mark, and it carries no raw Hyper-V number. A line that
/// overruns is cut mid-word by the card; a line carrying a code reads as "error 0x8007" and tells
/// nobody anything.
/// </summary>
public class VmFailureTextTests
{
    /// <summary>The whole vendor range Hyper-V answers a refused request with, plus the CIM values that
    /// share its meanings and two numbers nothing documents.</summary>
    private static readonly uint[] ReturnValues =
        [1, 2, 3, 4, 5, 4097, 4099, 32768, 32769, 32770, 32771, 32772, 32773, 32774, 32775, 32776,
         32777, 32778, 40000, 0];

    private static readonly VmOpKind[] Kinds =
        [VmOpKind.Start, VmOpKind.Resume, VmOpKind.Pause, VmOpKind.Save, VmOpKind.Shutdown];

    private static IEnumerable<VmFailure> Every()
    {
        foreach (var kind in Kinds)
        {
            foreach (var value in ReturnValues) yield return VmFailureText.ForRequestRefusal(kind, value);
            yield return VmFailureText.ForJobFailure(kind, null);
            yield return VmFailureText.ForJobFailure(kind, "Insufficient system resources exist to complete the requested service.");
            yield return VmFailureText.ForJobFailure(kind, "The operation stopped for a reason nobody has written down anywhere at all.");
            yield return VmFailureText.ForNoResult(kind, TimeSpan.FromMinutes(5));
            yield return VmFailureText.ForNoResult(kind, TimeSpan.FromSeconds(30));
            yield return VmFailureText.ForRequestFault(kind);
            yield return VmFailureText.ForTrackingFault(kind);
            yield return VmFailureText.ServiceNotRunning(kind);
            yield return VmFailureText.MachineNotFound(kind);
            yield return VmFailureText.NotAttempted(kind, "Hyper-V was running but still not answering");
        }
        foreach (var value in ReturnValues) yield return VmFailureText.ForShutdownRefusal(value);
        yield return VmFailureText.NoShutdownSupport();
    }

    /// <summary>The card truncates past 30 characters and a dismissible failure spends two of them on
    /// its mark, so the budget is 28 and nothing may exceed it.</summary>
    [Fact]
    public void EveryCardLine_FitsBesideTheDismissMark()
    {
        foreach (var failure in Every())
        {
            Assert.InRange(failure.Card.Length, 1, VmFailureText.CardLimit);
            Assert.True($"{failure.Card} {VmFailureText.DismissMark}".Length <= 30,
                $"'{failure.Card}' plus the dismiss mark would be cut by the card.");
            Assert.NotEqual("", failure.Full.Trim());
        }
    }

    /// <summary>A number on the card is the defect this exists to stop. It stays useful at the end of
    /// the sentence behind the card, and in vm-power.log.</summary>
    [Fact]
    public void NoCardLine_CarriesARawCode()
    {
        var code = new Regex(@"0x[0-9A-Fa-f]|\b\d{4,}\b");
        foreach (var failure in Every())
            Assert.False(code.IsMatch(failure.Card), $"'{failure.Card}' shows a code rather than a reason.");
    }

    /// <summary>The meaning of 32775 was a comment in the source while the card showed the number.</summary>
    [Fact]
    public void AnInvalidStateRefusal_SaysSoInWords()
    {
        var failure = VmFailureText.ForRequestRefusal(VmOpKind.Start, 32775);

        Assert.Equal("Not possible in this state", failure.Card);
        Assert.Contains("not in a state where that change is possible", failure.Full, StringComparison.Ordinal);
        Assert.Contains("32775", failure.Full, StringComparison.Ordinal);
    }

    /// <summary>A deadline that lapses is not a verdict: the work usually finishes afterwards, and the
    /// wording has to say which operation and that it is probably still running.</summary>
    [Fact]
    public void ANoResultLine_NamesTheOperationAndSaysItMayStillBeRunning()
    {
        var failure = VmFailureText.ForNoResult(VmOpKind.Start, TimeSpan.FromMinutes(5));

        Assert.Equal("No result after 5 minutes", failure.Card);
        Assert.Contains("start", failure.Full, StringComparison.Ordinal);
        Assert.Contains("probably still running", failure.Full, StringComparison.Ordinal);
    }
}
