namespace WSGM.Plugin.Ir.Tests.Fakes;

internal sealed class FakeEndpoint : IIrEndpoint
{
    internal readonly List<string> RemoteCalls = [];
    internal bool Cancelled;
    internal IrRemoteCatalog Catalog = new([]);
    internal int CatalogReads;
    internal bool Disposed;
    internal bool FailPress;
    internal string Firmware = "0.4.0";
    internal int Identifications;
    internal (string Ssid, string Password, string Token)? Network;
    internal IrPayload? Sent;

    /// <summary>How many identity polls still report a running sequence.</summary>
    internal int SequencePolls;

    public IrEndpointIdentity? Identity { get; set; }

    public Task<IrEndpointIdentity> IdentifyAsync(CancellationToken token)
    {
        Identifications++;
        if (SequencePolls > 0)
        {
            SequencePolls--;
        }

        Identity = Describe();
        return Task.FromResult(Identity);
    }

    public Task<IrPayload> LearnAsync(TimeSpan timeout, CancellationToken token)
    {
        return Task.FromResult(new IrPayload(36000, [9000, 4500], "measured"));
    }

    public Task TransmitAsync(IrPayload payload, int repeats, int gapMs, CancellationToken token)
    {
        Sent = payload;
        return Task.CompletedTask;
    }

    public Task<IrEndpointIdentity> ConfigureNetworkAsync(string ssid, string password, string networkToken,
        CancellationToken token)
    {
        Network = (ssid, password, networkToken);
        return Task.FromResult(Describe());
    }

    public Task<IrRemoteCatalog> ListRemotesAsync(CancellationToken token)
    {
        CatalogReads++;
        return Task.FromResult(Catalog);
    }

    public Task PressAsync(string remote, string button, CancellationToken token)
    {
        RemoteCalls.Add($"press {remote}/{button}");
        return FailPress
            ? throw new IOException("The IR endpoint closed the network connection.")
            : Task.CompletedTask;
    }

    public Task ClimateAsync(string remote, IrClimateRequest request, CancellationToken token)
    {
        RemoteCalls.Add($"climate {remote} power={request.Power} {request.Mode} {request.Degrees:0} "
                        + $"{request.Fan} swing={request.ToggleSwing}");
        return Task.CompletedTask;
    }

    public Task RunSequenceAsync(string remote, string sequence, CancellationToken token)
    {
        RemoteCalls.Add($"run {remote}/{sequence}");
        return Task.CompletedTask;
    }

    public Task CancelAsync(CancellationToken token)
    {
        Cancelled = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private IrEndpointIdentity Describe()
    {
        return new IrEndpointIdentity("test", "fake", Firmware, 1, 1024, "wsgm-ir-abc123", 7521,
            Network is { Ssid.Length: > 0 }, Network is { Ssid.Length: > 0 },
            Network is { Ssid.Length: > 0 } ? "192.0.2.7" : "",
            80, false, Catalog.Remotes.Length, SequencePolls > 0);
    }
}
