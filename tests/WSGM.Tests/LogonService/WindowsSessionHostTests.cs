extern alias LogonService;
using System.Runtime.InteropServices;
using WindowsSessionHost = LogonService::WSGM.LogonService.WindowsSessionHost;
using WtsInfo = LogonService::WSGM.LogonService.Interop.NativeMethods.WtsInfoW;

namespace WSGM.Tests.LogonService;

public sealed class WindowsSessionHostTests
{
    [Theory]
    [InlineData("short")]
    [InlineData("invalid")]
    [InlineData("zero")]
    [InlineData("valid")]
    [InlineData("future")]
    public void EverySuccessfulResponseReleasesItsBufferOnce(string response)
    {
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var size = Marshal.SizeOf<WtsInfo>();
        var buffer = Marshal.AllocHGlobal(size);
        var frees = 0;
        try
        {
            var time = response switch
            {
                "invalid" => long.MaxValue,
                "zero" => 0,
                "future" => now.AddMinutes(1).ToFileTimeUtc(),
                _ => now.AddSeconds(-30).ToFileTimeUtc()
            };
            Marshal.StructureToPtr(new WtsInfo { LogonTime = time }, buffer, false);
            var bytes = (uint)(response == "short" ? size - 1 : size);

            var age = WindowsSessionHost.DecodeLogonAge(buffer, bytes, owned =>
            {
                Assert.Equal(buffer, owned);
                frees++;
            }, now);

            Assert.Equal(1, frees);
            if (response == "valid")
            {
                Assert.Equal(TimeSpan.FromSeconds(30), age);
            }
            else if (response == "future")
            {
                Assert.Equal(TimeSpan.Zero, age);
            }
            else
            {
                Assert.Null(age);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
