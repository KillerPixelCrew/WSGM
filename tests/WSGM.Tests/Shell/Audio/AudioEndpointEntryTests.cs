using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class AudioEndpointEntryTests
{
    [Fact]
    public void EndpointRowsOnlyNotifyWhenTheirFriendlyNameChanges()
    {
        var endpoint = new AudioEndpointEntry("id", "Speakers");
        var changes = new List<string>();
        endpoint.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");

        endpoint.Name = "Speakers";
        endpoint.Name = "Headset";

        Assert.Equal([nameof(AudioEndpointEntry.Name)], changes);
    }
}
