using System.Reflection;
using System.Runtime.InteropServices;
using WSGM.Device.Sdk.Windows;

namespace WSGM.Device.Sdk.Tests.Windows;

public sealed class LowLevelKeyboardHookTests
{
    [Fact]
    public void GetMessageUsesTheSignedResultAndPreservesTheWin32Error()
    {
        // GetMessage returns -1 on failure; an unsigned or bool import would read that as a message
        // and spin the hook thread.
        var method = Assert.IsType<MethodInfo>(typeof(LowLevelKeyboardHook).GetMethod(
            "GetMessage",
            BindingFlags.NonPublic | BindingFlags.Static), false);
        var import = Assert.IsType<LibraryImportAttribute>(
            method.GetCustomAttribute<LibraryImportAttribute>());

        Assert.Equal(typeof(int), method.ReturnType);
        Assert.True(import.SetLastError);
    }
}
