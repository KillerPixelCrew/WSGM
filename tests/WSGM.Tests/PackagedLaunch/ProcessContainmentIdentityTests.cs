using WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

public sealed class ProcessContainmentIdentityTests
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("unreadable")]
    [InlineData("reused")]
    [InlineData("matched")]
    [InlineData("refused")]
    public void OnlyAMatchedCreationIdentityCanDispatchAssignment(string state)
    {
        var stamp = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        DateTime? expected = state == "unknown" ? null : stamp;
        List<string> calls = [];
        var accepted = ProcessContainmentIdentity.TryAssign(expected, () =>
        {
            calls.Add("read opened identity");
            return state switch
            {
                "unreadable" => null,
                "reused" => stamp.AddSeconds(1),
                _ => stamp
            };
        }, () =>
        {
            calls.Add("assign opened handle");
            return state != "refused";
        });
        Assert.Equal(state == "matched", accepted);
        Assert.Equal(state == "unknown" ? Array.Empty<string>()
            : state is "matched" or "refused" ? ["read opened identity", "assign opened handle"]
            : ["read opened identity"], calls);
    }
}
