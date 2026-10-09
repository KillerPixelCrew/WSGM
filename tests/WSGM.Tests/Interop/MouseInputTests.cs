using System.Runtime.InteropServices;
using WSGM.Interop;

namespace WSGM.Tests.Interop;

public sealed class MouseInputTests
{
    [Fact]
    public void MouseEdgesUseTheSameFortyByteSendInputUnionAsTheKeyboard()
    {
        Assert.Equal(40, Marshal.SizeOf<NativeMethods.InputRecord>());
        Assert.Equal(32, Marshal.SizeOf<NativeMethods.MouseInputData>());
        Assert.Equal(8, Marshal.OffsetOf<NativeMethods.InputRecord>(nameof(NativeMethods.InputRecord.data)).ToInt32());
        var down = MouseInput.SecondaryButtonRecord(true);
        var up = MouseInput.SecondaryButtonRecord(false);
        Assert.Equal(0u, down.type);
        Assert.Equal(0x0008u, down.data.mouse.flags);
        Assert.Equal(0u, up.type);
        Assert.Equal(0x0010u, up.data.mouse.flags);
        Assert.Equal(0, down.data.mouse.x);
        Assert.Equal(0, down.data.mouse.y);
        Assert.Equal(0u, down.data.mouse.mouseData);
    }
}
