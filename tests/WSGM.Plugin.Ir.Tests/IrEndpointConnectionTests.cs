using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace WSGM.Plugin.Ir.Tests;

public sealed class IrEndpointConnectionTests
{
    private const string Identity =
        "\"identity\":\"e072a115ef50\",\"model\":\"xiao-ir-mate\",\"firmware\":\"0.2.0\",\"protocol\":2,\"maxTimings\":1024";

    /// <summary>Builds one reply line, so a nested payload does not have to be brace-escaped.</summary>
    private static string Frame(string request, string status, string? data = null)
    {
        return "{\"v\":2,\"id\":\"" + Id(request) + "\",\"status\":\"" + status + "\""
               + (data is null ? "" : ",\"data\":" + data) + "}\n";
    }

    [Fact]
    public async Task IdentifySkipsBootNoiseAndForeignFramesAndSendsThePairingTokenOverTheNetwork()
    {
        ScriptedLink link = new(request =>
            "ESP-ROM:esp32c3-api1-20210207\r\n{\"v\":2,\"id\":\"other\",\"status\":\"ok\"}\nnot json\n"
            + $"{{\"v\":2,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity},\"hostname\":\"wsgm-ir-15ef50\",\"port\":7521,"
            + "\"wifiConfigured\":true,\"wifiConnected\":true,\"ip\":\"192.0.2.7\",\"learning\":false,\"uptimeMs\":12}}\n");
        IrEndpointConnection endpoint = new(_ => link, "0123456789abcdef");
        var identity = await endpoint.IdentifyAsync(CancellationToken.None);
        Assert.Equal("wsgm-ir-15ef50", identity.Hostname);
        Assert.True(identity.WifiConnected);
        Assert.Equal("192.0.2.7", identity.Ip);
        Assert.Same(identity, endpoint.Identity);
        using var sentDocument = JsonDocument.Parse(link.Written.Single());
        var sent = sentDocument.RootElement;
        Assert.Equal(IrEndpointConnection.ProtocolVersion, sent.GetProperty("v").GetInt32());
        Assert.Equal("identify", sent.GetProperty("op").GetString());
        Assert.Equal("0123456789abcdef", sent.GetProperty("token").GetString());
        Assert.DoesNotContain('\n', link.Written.Single());
    }

    [Fact]
    public async Task OlderFirmwareWithoutNetworkFieldsReadsAsUnconfigured()
    {
        ScriptedLink link = new(request =>
            $"{{\"v\":2,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity.Replace("0.2.0", "0.1.0")}}}}}\n");
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
        const string catalogText = "{\"remotes\":["
                                   + "{\"id\":\"hdmi-switch\",\"name\":\"HDMI switch ✓\",\"buttons\":[{\"id\":\"port-1\",\"label\":\"Port 1\"}],"
                                   + "\"sequences\":[{\"id\":\"reset\",\"label\":\"Reset\"}]},"
                                   + "{\"id\":\"ac\",\"name\":\"AC\",\"buttons\":[],\"sequences\":[],\"climate\":"
                                   + "{\"protocol\":\"MIDEA\",\"modes\":[\"cool\"],\"fans\":[\"auto\"],\"minDegrees\":17,\"maxDegrees\":30,"
                                   + "\"celsius\":true,\"swing\":\"toggle\"}}]}";
        // Three chunks, the first ending right after the multi-byte character in a label.
        var split = catalogText.IndexOf('✓') + 1;
        string[] chunks = [catalogText[..split], catalogText[split..(split + 40)], catalogText[(split + 40)..]];
        List<string> operations = [];
        ScriptedLink link = new(request =>
        {
            var operation = Op(request);
            operations.Add(operation);
            return operation switch
            {
                "identify" => Frame(request, "ok", "{" + Identity
                                                       + ",\"webPort\":80,\"webConfigured\":true,\"remotes\":3,\"sequenceRunning\":true}"),
                "remotes" => Frame(request, "ok", "{\"chunk\":" + JsonSerializer.Serialize(chunks[ChunkIndex(request)])
                                                  + ",\"index\":" + ChunkIndex(request) + ",\"count\":3}"),
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
        Assert.Equal("HDMI switch ✓", catalog.Remotes[0].Name);
        Assert.Equal("reset", catalog.Remotes[0].Sequences.Single().Id);
        Assert.Equal("toggle", catalog.Remotes[1].Climate!.Swing);
        Assert.Equal(30, catalog.Remotes[1].Climate!.MaxDegrees);

        await endpoint.PressAsync("hdmi-switch", "port-1", CancellationToken.None);
        await endpoint.ClimateAsync("ac", new IrClimateRequest(true, "cool", 20, "auto", true), CancellationToken.None);
        await endpoint.RunSequenceAsync("hdmi-switch", "reset", CancellationToken.None);
        await endpoint.CancelAsync(CancellationToken.None);

        Assert.Equal(["identify", "remotes", "remotes", "remotes", "press", "climate", "run", "cancel"], operations);
        Assert.Equal([0, 1, 2], link.Written.Where(frame => Op(frame) == "remotes").Select(ChunkIndex));
        using var climateDocument = JsonDocument.Parse(link.Written[5]);
        var climate = climateDocument.RootElement;
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

        var refusal =
            await Assert.ThrowsAsync<IrRejectedException>(() => endpoint.ListRemotesAsync(CancellationToken.None));

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
        ScriptedLink link = new(request =>
            $"{{\"v\":2,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity.Replace("\"protocol\":2", "\"protocol\":1")}}}}}\n");
        IrEndpointConnection endpoint = new(_ =>
        {
            opened++;
            return link;
        });
        var refusal =
            await Assert.ThrowsAsync<InvalidDataException>(() => endpoint.IdentifyAsync(CancellationToken.None));
        Assert.Contains("protocol 1 is incompatible", refusal.Message);
        Assert.Contains("0.5.0", refusal.Message);
        Assert.Null(endpoint.Identity);
        Assert.True(link.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            endpoint.TransmitAsync(new IrPayload(38000, [9000, 4500]), 0, 40, CancellationToken.None));
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task RefusalStatusesBecomeReadableMessagesAndForgetTheIdentity()
    {
        ScriptedLink link = new(request => Op(request) == "identify"
            ? $"{{\"v\":2,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity}}}}}\n"
            : $"{{\"v\":2,\"id\":\"{Id(request)}\",\"status\":\"timeout\"}}\n");
        IrEndpointConnection endpoint = new(_ => link);
        await endpoint.IdentifyAsync(CancellationToken.None);
        // Every status but the expected one is answered before the endpoint emits, so it is a refusal.
        var refusal = await Assert.ThrowsAsync<IrRejectedException>(() =>
            endpoint.LearnAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.StartsWith("No IR signal arrived", refusal.Message);
        Assert.Null(endpoint.Identity);
        Assert.Equal("Wi-Fi setup is only accepted over the USB connection.",
            IrEndpointConnection.Describe("wifi", "usb-only"));
        Assert.Equal("IR endpoint refused send: invalid-payload.",
            IrEndpointConnection.Describe("send", "invalid-payload"));
    }

    [Fact]
    public async Task CancellingALearnSendsOneCancelFrameAndDropsTheLink()
    {
        using CancellationTokenSource cancellation = new();
        ScriptedLink link = new(request =>
        {
            if (Op(request) == "identify")
            {
                return $"{{\"v\":2,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity}}}}}\n";
            }

            if (Op(request) == "learn")
            {
                cancellation.Cancel();
            }

            return null;
        });
        IrEndpointConnection endpoint = new(_ => link, "0123456789abcdef");
        await endpoint.IdentifyAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            endpoint.LearnAsync(TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.Equal(["identify", "learn", "cancel"], link.Written.Select(Op));
        using var cancelDocument = JsonDocument.Parse(link.Written[2]);
        Assert.Equal("0123456789abcdef", cancelDocument.RootElement.GetProperty("token").GetString());
        Assert.True(link.Disposed);
        Assert.Null(endpoint.Identity);
    }

    [Fact]
    public async Task OversizedResponseAndWrongStatusAreRejected()
    {
        ScriptedLink oversized = new(_ => new string('x', 32769));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new IrEndpointConnection(_ => oversized).IdentifyAsync(CancellationToken.None));
        ScriptedLink wrongStatus = new(request => Op(request) == "identify"
            ? $"{{\"v\":2,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity}}}}}\n"
            : $"{{\"v\":2,\"id\":\"{Id(request)}\",\"status\":\"ok\"}}\n");
        IrEndpointConnection endpoint = new(_ => wrongStatus);
        await endpoint.IdentifyAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IrRejectedException>(() =>
            endpoint.TransmitAsync(new IrPayload(38000, [9000, 4500]), 0, 40, CancellationToken.None));
    }

    [Fact]
    public async Task AnotherProtocolIsNotUnderstoodUnlessItReportsTheMismatch()
    {
        ScriptedLink link = new(request => Op(request) switch
        {
            "identify" => Frame(request, "ok", "{" + Identity + "}"),
            "send" => Frame(request, "transmitted").Replace("\"v\":2", "\"v\":1"),
            _ => Frame(request, "protocol-mismatch").Replace("\"v\":2", "\"v\":1")
        });
        IrEndpointConnection endpoint = new(_ => link);
        await endpoint.IdentifyAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            endpoint.TransmitAsync(new IrPayload(38000, [9000, 4500]), 0, 40, CancellationToken.None));
        await endpoint.IdentifyAsync(CancellationToken.None);
        var refusal = await Assert.ThrowsAsync<IrRejectedException>(() =>
            endpoint.PressAsync("tv", "power", CancellationToken.None));
        Assert.Contains("flash the firmware", refusal.Message);
    }

    [Fact]
    public async Task TransmitAndNetworkSetupCarryTheirArgumentsVerbatim()
    {
        ScriptedLink link = new(request => Op(request) switch
        {
            "send" => $"{{\"v\":2,\"id\":\"{Id(request)}\",\"status\":\"transmitted\"}}\n",
            _ =>
                $"{{\"v\":2,\"id\":\"{Id(request)}\",\"status\":\"ok\",\"data\":{{{Identity},\"hostname\":\"wsgm-ir-15ef50\",\"wifiConfigured\":true}}}}\n"
        });
        IrEndpointConnection endpoint = new(_ => link);
        await endpoint.IdentifyAsync(CancellationToken.None);
        await endpoint.TransmitAsync(new IrPayload(36000, [9000, 4500, 560], "manual"), 2, 45, CancellationToken.None);
        using var sendDocument = JsonDocument.Parse(link.Written[1]);
        var send = sendDocument.RootElement;
        Assert.Equal(36000, send.GetProperty("payload").GetProperty("carrierHz").GetInt32());
        Assert.Equal(3, send.GetProperty("payload").GetProperty("timingsUs").GetArrayLength());
        Assert.Equal(2, send.GetProperty("repeats").GetInt32());
        Assert.Equal(45, send.GetProperty("gapMs").GetInt32());
        var identity =
            await endpoint.ConfigureNetworkAsync("Home", "pässwörd", "0123456789abcdef", CancellationToken.None);
        Assert.True(identity.WifiConfigured);
        using var wifiDocument = JsonDocument.Parse(link.Written[2]);
        var wifi = wifiDocument.RootElement;
        Assert.Equal("wifi", wifi.GetProperty("op").GetString());
        Assert.Equal("pässwörd", wifi.GetProperty("password").GetString());
        Assert.Equal("0123456789abcdef", wifi.GetProperty("token").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            endpoint.ConfigureNetworkAsync("Home", "x", "short", CancellationToken.None));
    }

    [Theory]
    [InlineData(32768, false)]
    [InlineData(32769, true)]
    public async Task ResponseLimitCountsUtf8BytesIncludingSurrogatePairs(int bytes, bool rejected)
    {
        ScriptedLink link = new(request =>
        {
            var empty = Frame(request, "ok", "{" + Identity + ",\"padding\":\"\"}");
            var remaining = bytes - Encoding.UTF8.GetByteCount(empty.TrimEnd('\n'));
            var padding = string.Concat(Enumerable.Repeat("😀", remaining / 4))
                          + new string('x', remaining % 4);
            var reply = empty.Replace("\"padding\":\"\"", "\"padding\":\"" + padding + "\"");
            Assert.Equal(bytes, Encoding.UTF8.GetByteCount(reply.TrimEnd('\n')));
            Assert.True(reply.Length < 32768);
            return reply;
        });
        await using IrEndpointConnection endpoint = new(_ => link);
        if (rejected)
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                endpoint.IdentifyAsync(CancellationToken.None));
            Assert.Equal("IR response exceeds frame limit.", failure.Message);
            Assert.True(link.Disposed);
            Assert.Null(endpoint.Identity);
        }
        else
        {
            var identity = await endpoint.IdentifyAsync(CancellationToken.None);
            Assert.Equal("e072a115ef50", identity.Identity);
            Assert.False(link.Disposed);
        }

        Assert.Single(link.Written);
    }

    [Theory]
    [InlineData("id", "7")]
    [InlineData("v", "\"one\"")]
    [InlineData("v", null)]
    [InlineData("status", "false")]
    [InlineData("status", null)]
    public async Task MalformedReplyDropsTheLinkWithoutResending(string property, string? value)
    {
        ScriptedLink link = new(request =>
        {
            var reply = Frame(request, "ok", "{" + Identity + "}");
            var original = property switch
            {
                "id" => "\"id\":\"" + Id(request) + "\"",
                "v" => "\"v\":2",
                _ => "\"status\":\"ok\""
            };
            return value is null
                ? reply.Replace(original + ",", "")
                : reply.Replace(original, "\"" + property + "\":" + value);
        });
        await using IrEndpointConnection endpoint = new(_ => link);
        var failure = await Record.ExceptionAsync(() => endpoint.IdentifyAsync(CancellationToken.None));

        Assert.True(failure is InvalidOperationException or KeyNotFoundException, failure?.ToString());
        Assert.True(link.Disposed);
        Assert.Null(endpoint.Identity);
        Assert.Single(link.Written);
    }

    [Fact]
    public async Task StaleReplyAndMissingIdDoNotConsumeTheMatchingReply()
    {
        ScriptedLink link = new(request =>
            "{\"v\":2,\"status\":\"ok\"}\n"
            + "{\"v\":2,\"id\":\"stale\",\"status\":\"ok\"}\n"
            + Frame(request, "ok", "{" + Identity + "}"));
        await using IrEndpointConnection endpoint = new(_ => link);

        var identity = await endpoint.IdentifyAsync(CancellationToken.None);

        Assert.Equal("e072a115ef50", identity.Identity);
        Assert.Same(identity, endpoint.Identity);
        Assert.Single(link.Written);
        Assert.False(link.Disposed);
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
                using var requestDocument = JsonDocument.Parse(request);
                var element = requestDocument.RootElement;
                var id = element.GetProperty("id").GetString()!;
                var authorized = element.TryGetProperty("token", out var token) &&
                                 token.GetString() == "0123456789abcdef";
                await Task.Delay(TcpIrLink.ReceiveTimeoutMs *
                                 2); // Longer than the receive timeout, so idle polling runs.
                await writer.WriteLineAsync(element.GetProperty("op").GetString() == "identify"
                    ? $"{{\"v\":2,\"id\":\"{id}\",\"status\":\"ok\",\"data\":{{{Identity},\"hostname\":\"wsgm-ir-15ef50\",\"wifiConnected\":true,\"ip\":\"127.0.0.1\"}}}}"
                    : authorized
                        ? $"{{\"v\":2,\"id\":\"{id}\",\"status\":\"transmitted\"}}"
                        : $"{{\"v\":2,\"id\":\"{id}\",\"status\":\"unauthorized\"}}");
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

    private static string Id(string frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static string Op(string frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("op").GetString()!;
    }

    private static int ChunkIndex(string frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("chunk").GetInt32();
    }

    /// <summary>Answers each written frame from a script; idle reads return -1 like a real link.</summary>
    private sealed class ScriptedLink(Func<string, string?> respond) : IIrLink
    {
        internal readonly List<string> Written = [];
        private readonly Queue<char> _incoming = new();
        internal bool Disposed;

        public void WriteLine(string frame)
        {
            Written.Add(frame);
            if (respond(frame) is not { } response)
            {
                return;
            }

            foreach (var character in response)
            {
                _incoming.Enqueue(character);
            }
        }

        public int ReadChar()
        {
            return _incoming.Count > 0 ? _incoming.Dequeue() : -1;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
