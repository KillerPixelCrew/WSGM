using System.Text.Json;
using Xunit;

namespace WSGM.Plugin.Ir.Tests;

public sealed class IrEndpointConnectionTests
{
    private const string Identity = "\"identity\":\"e072a115ef50\",\"model\":\"xiao-ir-mate\",\"firmware\":\"0.2.0\",\"protocol\":1,\"maxTimings\":1024";

    [Fact]
    public async Task IdentifySkipsBootNoiseAndForeignFramesAndSendsThePairingTokenOverTheNetwork()
    {
        ScriptedLink link = new(request => "ESP-ROM:esp32c3-api1-20210207\r\n{\"v\":1,\"id\":\"other\",\"status\":\"ok\"}\nnot json\n"
            + $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity},\"hostname\":\"wsgm-ir-15ef50\",\"port\":7521,"
            + "\"wifiConfigured\":true,\"wifiConnected\":true,\"ip\":\"192.0.2.7\",\"learning\":false,\"uptimeMs\":12}}\n");
        IrEndpointConnection endpoint = new(_ => link, "0123456789abcdef");
        IrEndpointIdentity identity = await endpoint.IdentifyAsync(default);
        Assert.Equal("wsgm-ir-15ef50", identity.Hostname);
        Assert.True(identity.WifiConnected);
        Assert.Equal("192.0.2.7", identity.Ip);
        Assert.Same(identity, endpoint.Identity);
        JsonElement sent = JsonDocument.Parse(link.Written.Single()).RootElement;
        Assert.Equal(1, sent.GetProperty("v").GetInt32());
        Assert.Equal("identify", sent.GetProperty("op").GetString());
        Assert.Equal("0123456789abcdef", sent.GetProperty("token").GetString());
        Assert.DoesNotContain('\n', link.Written.Single());
    }

    [Fact]
    public async Task OlderFirmwareWithoutNetworkFieldsReadsAsUnconfigured()
    {
        ScriptedLink link = new(request => $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity.Replace("0.2.0", "0.1.0")}}}}}\n");
        IrEndpointConnection endpoint = new(_ => link);
        IrEndpointIdentity identity = await endpoint.IdentifyAsync(default);
        Assert.Equal("0.1.0", identity.Firmware);
        Assert.False(identity.WifiConfigured);
        Assert.Null(identity.Hostname);
        Assert.DoesNotContain("token", link.Written.Single());
    }

    [Fact]
    public async Task IncompatibleProtocolIsRefusedExplicitlyAndDropsTheLink()
    {
        int opened = 0;
        ScriptedLink link = new(request => $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity.Replace("\"protocol\":1", "\"protocol\":2")}}}}}\n");
        IrEndpointConnection endpoint = new(_ => { opened++; return link; });
        InvalidDataException refusal = await Assert.ThrowsAsync<InvalidDataException>(() => endpoint.IdentifyAsync(default));
        Assert.Contains("protocol 2", refusal.Message);
        Assert.Null(endpoint.Identity);
        Assert.True(link.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => endpoint.TransmitAsync(new(38000, [9000, 4500]), 0, 40, default));
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task RefusalStatusesBecomeReadableMessagesAndForgetTheIdentity()
    {
        ScriptedLink link = new(request => Op(request) == "identify"
            ? $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity}}}}}\n"
            : $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"timeout\"}}\n");
        IrEndpointConnection endpoint = new(_ => link);
        await endpoint.IdentifyAsync(default);
        InvalidDataException refusal = await Assert.ThrowsAsync<InvalidDataException>(() => endpoint.LearnAsync(TimeSpan.FromSeconds(1), default));
        Assert.StartsWith("No IR signal arrived", refusal.Message);
        Assert.Null(endpoint.Identity);
        Assert.Equal("Wi-Fi setup is only accepted over the USB connection.", IrEndpointConnection.Describe("wifi", "usb-only"));
        Assert.Equal("IR endpoint refused send: invalid-payload.", IrEndpointConnection.Describe("send", "invalid-payload"));
    }

    [Fact]
    public async Task CancellingALearnSendsOneCancelFrameAndDropsTheLink()
    {
        using CancellationTokenSource cancellation = new();
        ScriptedLink link = new(request =>
        {
            if (Op(request) == "identify") { return $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity}}}}}\n"; }
            if (Op(request) == "learn") { cancellation.Cancel(); }
            return null;
        });
        IrEndpointConnection endpoint = new(_ => link, "0123456789abcdef");
        await endpoint.IdentifyAsync(default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => endpoint.LearnAsync(TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.Equal(["identify", "learn", "cancel"], link.Written.Select(Op));
        Assert.Equal("0123456789abcdef", JsonDocument.Parse(link.Written[2]).RootElement.GetProperty("token").GetString());
        Assert.True(link.Disposed);
        Assert.Null(endpoint.Identity);
    }

    [Fact]
    public async Task OversizedResponseAndWrongStatusAreRejected()
    {
        ScriptedLink oversized = new(_ => new string('x', 32769));
        await Assert.ThrowsAsync<InvalidDataException>(() => new IrEndpointConnection(_ => oversized).IdentifyAsync(default));
        ScriptedLink wrongStatus = new(request => Op(request) == "identify"
            ? $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity}}}}}\n"
            : $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\"}}\n");
        IrEndpointConnection endpoint = new(_ => wrongStatus);
        await endpoint.IdentifyAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(() => endpoint.TransmitAsync(new(38000, [9000, 4500]), 0, 40, default));
    }

    [Fact]
    public async Task TransmitAndNetworkSetupCarryTheirArgumentsVerbatim()
    {
        ScriptedLink link = new(request => Op(request) switch
        {
            "send" => $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"transmitted\"}}\n",
            _ => $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity},\"hostname\":\"wsgm-ir-15ef50\",\"wifiConfigured\":true}}}}\n",
        });
        IrEndpointConnection endpoint = new(_ => link);
        await endpoint.IdentifyAsync(default);
        await endpoint.TransmitAsync(new(36000, [9000, 4500, 560], "manual"), 2, 45, default);
        JsonElement send = JsonDocument.Parse(link.Written[1]).RootElement;
        Assert.Equal(36000, send.GetProperty("payload").GetProperty("carrierHz").GetInt32());
        Assert.Equal(3, send.GetProperty("payload").GetProperty("timingsUs").GetArrayLength());
        Assert.Equal(2, send.GetProperty("repeats").GetInt32());
        Assert.Equal(45, send.GetProperty("gapMs").GetInt32());
        IrEndpointIdentity identity = await endpoint.ConfigureNetworkAsync("Home", "pässwörd", "0123456789abcdef", default);
        Assert.True(identity.WifiConfigured);
        JsonElement wifi = JsonDocument.Parse(link.Written[2]).RootElement;
        Assert.Equal("wifi", wifi.GetProperty("op").GetString());
        Assert.Equal("pässwörd", wifi.GetProperty("password").GetString());
        Assert.Equal("0123456789abcdef", wifi.GetProperty("token").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => endpoint.ConfigureNetworkAsync("Home", "x", "short", default));
    }

    private static string Id(string frame) => JsonDocument.Parse(frame).RootElement.GetProperty("id").GetString()!;
    private static string Op(string frame) => JsonDocument.Parse(frame).RootElement.GetProperty("op").GetString()!;

    /// <summary>Answers each written frame from a script; idle reads return -1 like a real link.</summary>
    private sealed class ScriptedLink(Func<string, string?> respond) : IIrLink
    {
        internal readonly List<string> Written = [];
        internal bool Disposed;
        private readonly Queue<char> _incoming = new();

        public void WriteLine(string frame)
        {
            Written.Add(frame);
            if (respond(frame) is { } response) { foreach (char character in response) { _incoming.Enqueue(character); } }
        }

        public int ReadChar() => _incoming.Count > 0 ? _incoming.Dequeue() : -1;
        public void Dispose() => Disposed = true;
    }
}
