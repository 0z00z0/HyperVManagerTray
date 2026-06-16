using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

public class NativeMethodsTests
{
    // The Win32 task-dialog structs are byte-packed (commctrl.h wraps them in pshpack1.h).
    // If they're declared with natural 8-byte alignment instead, TaskDialogIndirect rejects
    // the call with E_INVALIDARG and silently shows nothing — i.e. "Check for updates" does
    // nothing. These pin the x64 marshalled sizes so that regression can't come back.

    [Fact]
    public void TaskDialogConfig_HasPackedX64Size()
        => Assert.Equal(160, NativeMethods.TaskDialogConfigSize);

    [Fact]
    public void TaskDialogButton_HasPackedX64Size()
        => Assert.Equal(12, NativeMethods.TaskDialogButtonSize);
}
