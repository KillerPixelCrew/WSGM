using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace WSGM.Plugin.Ir.Tests;

public sealed class IrEndpointConnectionTests
{
    private const string Identity = "\"identity\":\"e072a115ef50\",\"model\":\"xiao-ir-mate\",\"firmware\":\"0.2.0\",\"protocol\":1,\"maxTimings\":1024";

    /// <summary>Builds one reply line, so a nested payload does not have to be brace-escaped.</summary>
    private static string Frame(string request, string status, string? data = null) =>
        "{\"v\":1,\"id\":\"" + Id(request) + "\",\"status\":\"" + status + "\""
        + (data is null ? "" : ",\"data\":" + data) + "}\n";

    [Fact]
    public async Task IdentifySkipsBootNoiseAndForeignFramesAndSendsThePairingTokenOverTheNetwork()
    {
        ScriptedLink link = new(request => "ESP-ROM:esp32c3-api1-20210207\r\n{\"v\":1,\"id\":\"other\",\"status\":\"ok\"}\nnot json\n"
            + $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity},\"hostname\":\"wsgm-ir-15ef50\",\"port\":7521,"
            + "\"wifiConfigured\":true,\"wifiConnected\":true,\"ip\":\"192.0.2.7\",\"learning\":false,\"uptimeMs\":12}}\n");
        IrEndpointConnection endpoint = new(_ => link, "0123456789abcdef");
        var identity = await endpoint.IdentifyAsync(CancellationToken.None);
        Assert.Equal("wsgm-ir-15ef50", identity.Hostname);
        Assert.True(identity.WifiConnected);
        Assert.Equal("192.0.2.7", identity.Ip);
        Assert.Same(identity, endpoint.Identity);
        var sent = JsonDocument.Parse(link.Written.Single()).RootElement;
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
        var identity = await endpoint.IdentifyAsync(CancellationToken.None);
        Assert.Equal("0.1.0", identity.Firmware);
        Assert.False(identity.WifiConfigured);
        Assert.Null(identity.Hostname);
        Assert.DoesNotContain("token", link.Written.Single());
    }

    [Fact]
    public async Task BuiltInRemoteOperationsCarryTheirOwnFramesAndExpectedStatuses()
    {
        List<string> operations = [];
        ScriptedLink link = new(request =>
        {
            var operation = Op(request);
            operations.Add(operation);
            return operation switch
            {
                "identify" => Frame(request, "ok", "{" + Identity
                    + ",\"webPort\":80,\"webConfigured\":true,\"remotes\":3,\"sequenceRunning\":true}"),
                "remotes" => Frame(request, "ok", "{\"remotes\":["
                    + "{\"id\":\"hdmi-switch\",\"name\":\"HDMI switch\",\"buttons\":[{\"id\":\"port-1\",\"label\":\"Port 1\"}],"
                    + "\"sequences\":[{\"id\":\"reset\",\"label\":\"Reset\"}]},"
                    + "{\"id\":\"ac\",\"name\":\"AC\",\"buttons\":[],\"sequences\":[],\"climate\":"
                    + "{\"protocol\":\"MIDEA\",\"modes\":[\"cool\"],\"fans\":[\"auto\"],\"minDegrees\":17,\"maxDegrees\":30,"
                    + "\"celsius\":true,\"swing\":\"toggle\"}}]}"),
                // A sequence only reports that it started; press and climate report an emission.
                "run" => Frame(request, "started"),
                "cancel" => Frame(request, "ok"),
                _ => Frame(request, "transmitted")
            };
        });
        IrEndpointConnection endpoint = new(_ => link);

        var identity = await endpoint.IdentifyAsync(CancellationToken.None);
        Assert.Equal(80, identity.WebPort);
        Assert.True(identity.WebConfigured);
        Assert.Equal(3, identity.Remotes);
        Assert.True(identity.SequenceRunning);

        var catalog = await endpoint.ListRemotesAsync(CancellationToken.None);
        Assert.Equal(["hdmi-switch", "ac"], catalog.Remotes.Select(remote => remote.Id));
        Assert.Equal("reset", catalog.Remotes[0].Sequences.Single().Id);
        Assert.Equal("toggle", catalog.Remotes[1].Climate!.Swing);
        Assert.Equal(30, catalog.Remotes[1].Climate!.MaxDegrees);

        await endpoint.PressAsync("hdmi-switch", "port-1", CancellationToken.None);
        await endpoint.ClimateAsync("ac", new IrClimateRequest(true, "cool", 20, "auto", ToggleSwing: true), CancellationToken.None);
        await endpoint.RunSequenceAsync("hdmi-switch", "reset", CancellationToken.None);
        await endpoint.CancelAsync(CancellationToken.None);

        Assert.Equal(["identify", "remotes", "press", "climate", "run", "cancel"], operations);
        var climate = JsonDocument.Parse(link.Written[3]).RootElement;
        Assert.Equal("ac", climate.GetProperty("remote").GetString());
        Assert.True(climate.GetProperty("toggleSwing").GetBoolean());
    }

    [Fact]
    public async Task OlderFirmwareRefusesTheRemoteOperationsByName()
    {
        ScriptedLink link = new(request => Op(request) == "identify"
            ? Frame(request, "ok", "{" + Identity + "}")
            : Frame(request, "unsupported-operation"));
        IrEndpointConnection endpoint = new(_ => link);
        await endpoint.IdentifyAsync(CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<InvalidDataException>(
            () => endpoint.ListRemotesAsync(CancellationToken.None));

        Assert.Contains("0.4.0", refusal.Message);
        Assert.Equal("The endpoint has no remote with that id. Read its built-in remotes first.",
            IrEndpointConnection.Describe("press", "unknown-remote"));
        Assert.StartsWith("The endpoint is still learning or running a sequence",
            IrEndpointConnection.Describe("run", "busy"));
    }

    [Fact]
    public async Task IncompatibleProtocolIsRefusedExplicitlyAndDropsTheLink()
    {
        var opened = 0;
        ScriptedLink link = new(request => $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity.Replace("\"protocol\":1", "\"protocol\":2")}}}}}\n");
        IrEndpointConnection endpoint = new(_ => { opened++; return link; });
        var refusal = await Assert.ThrowsAsync<InvalidDataException>(() => endpoint.IdentifyAsync(CancellationToken.None));
        Assert.Contains("protocol 2", refusal.Message);
        Assert.Null(endpoint.Identity);
        Assert.True(link.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => endpoint.TransmitAsync(new IrPayload(38000, [9000, 4500]), 0, 40, CancellationToken.None));
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task RefusalStatusesBecomeReadableMessagesAndForgetTheIdentity()
    {
        ScriptedLink link = new(request => Op(request) == "identify"
            ? $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity}}}}}\n"
            : $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"timeout\"}}\n");
        IrEndpointConnection endpoint = new(_ => link);
        await endpoint.IdentifyAsync(CancellationToken.None);
        var refusal = await Assert.ThrowsAsync<InvalidDataException>(() => endpoint.LearnAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
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
        await endpoint.IdentifyAsync(CancellationToken.None);
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
        await Assert.ThrowsAsync<InvalidDataException>(() => new IrEndpointConnection(_ => oversized).IdentifyAsync(CancellationToken.None));
        ScriptedLink wrongStatus = new(request => Op(request) == "identify"
            ? $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity}}}}}\n"
            : $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\"}}\n");
        IrEndpointConnection endpoint = new(_ => wrongStatus);
        await endpoint.IdentifyAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => endpoint.TransmitAsync(new IrPayload(38000, [9000, 4500]), 0, 40, CancellationToken.None));
    }

    [Fact]
    public async Task TransmitAndNetworkSetupCarryTheirArgumentsVerbatim()
    {
        ScriptedLink link = new(request => Op(request) switch
        {
            "send" => $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"transmitted\"}}\n",
            _ => $"{{\"v\":1,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity},\"hostname\":\"wsgm-ir-15ef50\",\"wifiConfigured\":true}}}}\n"
        });
        IrEndpointConnection endpoint = new(_ => link);
        await endpoint.IdentifyAsync(CancellationToken.None);
        await endpoint.TransmitAsync(new IrPayload(36000, [9000, 4500, 560], "manual"), 2, 45, CancellationToken.None);
        var send = JsonDocument.Parse(link.Written[1]).RootElement;
        Assert.Equal(36000, send.GetProperty("payload").GetProperty("carrierHz").GetInt32());
        Assert.Equal(3, send.GetProperty("payload").GetProperty("timingsUs").GetArrayLength());
        Assert.Equal(2, send.GetProperty("repeats").GetInt32());
        Assert.Equal(45, send.GetProperty("gapMs").GetInt32());
        var identity = await endpoint.ConfigureNetworkAsync("Home", "pässwörd", "0123456789abcdef", CancellationToken.None);
        Assert.True(identity.WifiConfigured);
        var wifi = JsonDocument.Parse(link.Written[2]).RootElement;
        Assert.Equal("wifi", wifi.GetProperty("op").GetString());
        Assert.Equal("pässwörd", wifi.GetProperty("password").GetString());
        Assert.Equal("0123456789abcdef", wifi.GetProperty("token").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => endpoint.ConfigureNetworkAsync("Home", "x", "short", CancellationToken.None));
    }

    [Fact]
    public async Task TcpLinkSpeaksTheProtocolAgainstALoopbackListenerAndFailsFastWhenClosed()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<string> received = [];
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            using StreamReader reader = new(peer.GetStream(), Encoding.UTF8);
            await using StreamWriter writer = new(peer.GetStream(), new UTF8Encoding(false));
            writer.AutoFlush = true;
            writer.NewLine = "\n";
            await writer.WriteLineAsync("ESP-ROM boot noise"); // Arrives before any request; must be ignored.
            for (var frames = 0; frames < 2; frames++)
            {
                var request = (await reader.ReadLineAsync())!;
                received.Add(request);
                var element = JsonDocument.Parse(request).RootElement;
                var id = element.GetProperty("id").GetString()!;
                var authorized = element.TryGetProperty("token", out var token) && token.GetString() == "0123456789abcdef";
                await Task.Delay(250); // Longer than the link's receive timeout, so idle polling is exercised.
                await writer.WriteLineAsync(element.GetProperty("op").GetString() == "identify"
                    ? $"{{\"v\":1,\"id\":\"{id}\",\"status\":\"ok\",\"data\":{{{Identity},\"hostname\":\"wsgm-ir-15ef50\",\"wifiConnected\":true,\"ip\":\"127.0.0.1\"}}}}"
                    : authorized ? $"{{\"v\":1,\"id\":\"{id}\",\"status\":\"transmitted\"}}" : $"{{\"v\":1,\"id\":\"{id}\",\"status\":\"unauthorized\"}}");
            }
        });
        var endpoint = IrEndpointConnection.Create(new IrEndpointTarget(true, $"127.0.0.1:{port}", "0123456789abcdef"));
        var identity = await endpoint.IdentifyAsync(CancellationToken.None);
        Assert.Equal("127.0.0.1", identity.Ip);
        await endpoint.TransmitAsync(new IrPayload(38000, [9000, 4500]), 0, 40, CancellationToken.None);
        await server;
        Assert.Equal(["identify", "send"], received.Select(Op));
        // The peer closed its side after two frames: the next exchange fails instead of hanging.
        await Assert.ThrowsAsync<IOException>(() => endpoint.IdentifyAsync(CancellationToken.None));
        Assert.Null(endpoint.Identity);
        await endpoint.DisposeAsync();
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
            if (respond(frame) is not { } response) { return; }
            foreach (var character in response) { _incoming.Enqueue(character); }
        }

        public int ReadChar() => _incoming.Count > 0 ? _incoming.Dequeue() : -1;
        public void Dispose() => Disposed = true;
    }
}
