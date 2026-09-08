using WindowsDeviceControl;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class BluetoothDeviceCatalogTests
{
    private const string Container = "e9b30f58-4b1d-4827-b9db-7c3826cad481";

    [Fact]
    public void ClassicAndLeEndpointsEnrichOneLogicalDeviceAndKeepActionEndpoints()
    {
        BluetoothDeviceCatalog catalog = new();
        Add(catalog, "classic", true, false, false, Container);
        var row = Assert.Single(Add(catalog, "le", false, true, true, "{" + Container.ToUpperInvariant() + "}"));
        Assert.True(row.Paired);
        Assert.True(row.Connected);
        Assert.Equal("classic", row.EndpointId);
        Assert.Equal("le", row.PairingEndpointId);
        Assert.Equal(2, row.EndpointIds.Count);
        Assert.Equal("container:" + Container, row.Id);
    }

    [Fact]
    public void SameNamesAndEmptyContainersDoNotMergeUnrelatedDevices()
    {
        BluetoothDeviceCatalog catalog = new();
        Add(catalog, "one", false, true, false, "");
        var rows = Add(catalog, "two", false, true, false, Guid.Empty.ToString());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("Controller", row.Name));
    }

    [Fact]
    public void RemovingOneTransportDoesNotDisconnectTheOther()
    {
        BluetoothDeviceCatalog catalog = new();
        Add(catalog, "classic", true, false, true, Container);
        Add(catalog, "le", true, false, true, Container);
        var rows = catalog.Apply(WindowsRadio.BluetoothChangeKind.Removed, new("classic", "", false, false, false, ""));
        Assert.True(Assert.Single(rows).Connected);
    }

    [Fact]
    public void RepeatedSweepsReplaceStaleEndpointsWithoutAccumulatingRows()
    {
        BluetoothDeviceCatalog catalog = new();
        Add(catalog, "old", false, true, true, Container);
        catalog.BeginSweep();
        Add(catalog, "new", false, true, false, Container);
        var rows = catalog.Apply(WindowsRadio.BluetoothChangeKind.EnumerationCompleted, default);
        var row = Assert.Single(rows);
        Assert.Equal("new", Assert.Single(row.EndpointIds));
        Assert.False(row.Connected);
        catalog.BeginSweep();
        Assert.Empty(catalog.Apply(WindowsRadio.BluetoothChangeKind.EnumerationCompleted, default));
    }

    [Fact]
    public void UnseenPairedDeviceRemainsKnownButOffline()
    {
        BluetoothDeviceCatalog catalog = new();
        Add(catalog, "paired", true, false, true, Container);
        catalog.BeginSweep();
        var row = Assert.Single(catalog.Apply(WindowsRadio.BluetoothChangeKind.EnumerationCompleted, default));
        Assert.True(row.Paired);
        Assert.False(row.Connected);
    }

    [Fact]
    public void LateContainerIdentityMergesPreviouslySeparateEndpoints()
    {
        BluetoothDeviceCatalog catalog = new();
        Add(catalog, "classic", true, false, false, "");
        Assert.Equal(2, Add(catalog, "le", false, true, false, Container).Count);
        var row = Assert.Single(Add(catalog, "classic", true, false, false, Container));
        Assert.Equal(2, row.EndpointIds.Count);
    }

    [Fact]
    public void ConfirmedUnpairDoesNotLeaveARetainedPairedGhost()
    {
        BluetoothDeviceCatalog catalog = new();
        Add(catalog, "paired", true, false, true, Container);
        catalog.ConfirmPairing("paired", false);
        Assert.Empty(catalog.Apply(WindowsRadio.BluetoothChangeKind.Removed, new("paired", "", false, false, false, "")));
    }

    private static IReadOnlyList<BluetoothLogicalDevice> Add(BluetoothDeviceCatalog catalog,
        string id, bool paired, bool canPair, bool connected, string container) =>
        catalog.Apply(WindowsRadio.BluetoothChangeKind.Added, new(id, "Controller", paired, canPair, connected, container));
}
