using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Browser;

namespace WSGM.BrowserProbe;

internal sealed partial class ProbeWindow
{
    private readonly bool _keyboardOnly;
    private int _keyboardDebugPort;
    private int _commandId;

    private const string KeyboardFixture = """
        <!doctype html><meta charset="utf-8"><style>
        body{font:20px system-ui;margin:24px}input,textarea{display:block;font:20px system-ui;width:500px;height:44px;margin:12px 0}
        </style><form><input id="plain" name="q" placeholder="Search"><button>Search</button></form>
        <input id="password" type="password"><textarea id="multiline"></textarea>
        <div id="rich" contenteditable style="width:500px;height:44px;border:1px solid gray"></div>
        """;

    private async Task CheckKeyboardAsync(Stream pipe, string address, CancellationToken token)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var targets = JsonDocument.Parse(await http.GetStringAsync(
            "http://127.0.0.1:" + _keyboardDebugPort + "/json/list", token));
        var target = targets.RootElement.EnumerateArray().Single(item => item.GetProperty("type").GetString() == "page");
        using ClientWebSocket socket = new();
        await socket.ConnectAsync(new Uri(target.GetProperty("webSocketDebuggerUrl").GetString()!), token);
        await CdpAsync(socket, "Runtime.enable", new { }, token);
        await NavigateAsync(pipe, address, token);
        await CdpAsync(socket, "Emulation.setTouchEmulationEnabled", new { enabled = true }, token);
        var isolated = await EvaluateAsync(socket, "typeof wsgmTextInput", token);
        if (isolated.GetString() != "undefined")
        {
            throw new InvalidOperationException("The keyboard binding was exposed to website scripts.");
        }

        foreach (var id in new[] { "plain", "password", "multiline", "rich" })
        {
            var geometry = await EvaluateAsync(socket,
                "(()=>{const n=document.getElementById('" + id + "'),r=n.getBoundingClientRect();return {x:r.x+30,y:r.y+r.height/2,mode:n.inputMode}})()", token);
            if (geometry.GetProperty("mode").GetString() != "none")
            {
                throw new InvalidOperationException(id + " can still summon the system keyboard.");
            }

            var points = new[] { new { x = geometry.GetProperty("x").GetDouble(), y = geometry.GetProperty("y").GetDouble(), id = 1 } };
            await CdpAsync(socket, "Input.dispatchTouchEvent", new { type = "touchStart", touchPoints = points }, token);
            await CdpAsync(socket, "Input.dispatchTouchEvent", new { type = "touchEnd", touchPoints = Array.Empty<object>() }, token);
            var keyboard = await ReadUntilAsync(pipe, packet => packet.Kind == "keyboard", token);
            if (!keyboard.Visible || keyboard.Sensitive != (id == "password") || keyboard.Multiline != (id is "multiline" or "rich"))
            {
                throw new InvalidOperationException("Wrong keyboard metadata for " + id);
            }

            await SendAsync(pipe, new BrowserPacket { Kind = "visibility", Visible = false }, token);
            await ReadUntilAsync(pipe, packet => packet.Kind == "state" && packet.Text.StartsWith("Retained", StringComparison.Ordinal), token);
            await SendAsync(pipe, new BrowserPacket { Kind = "visibility", Visible = true }, token);
            var text = id == "plain" ? "WSGM touch" : id == "password" ? "test-secret" : "first\nsecond";
            await SendAsync(pipe, new BrowserPacket
            {
                Kind = "text", Text = text, Submit = id == "plain", Replace = keyboard.Replace, NavigationId = keyboard.NavigationId
            }, token);
            if (id == "plain")
            {
                var submitted = await ReadUntilAsync(pipe, packet => packet.Kind == "address" && packet.Text.Contains("q=WSGM", StringComparison.Ordinal), token);
                if (!submitted.Text.EndsWith("q=WSGM+touch", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Enter did not submit the text into the website form.");
                }

                await ReadUntilAsync(pipe, packet => packet.Kind == "state" && !packet.Loading
                                                    && packet.NavigationId == submitted.NavigationId, token);
            }
            else
            {
                await SendAsync(pipe, new BrowserPacket { Kind = "capture" }, token);
                await ReadUntilAsync(pipe, packet => packet.Kind == "capture", token);
                var actual = await EvaluateAsync(socket, "document.getElementById('" + id + "')." + (id == "rich" ? "innerText" : "value"), token);
                if (actual.GetString() != text)
                {
                    throw new InvalidOperationException("Accepted text was lost from " + id);
                }
            }

            _results.Add(new { phase = "touch-keyboard", field = id, keyboard.Sensitive, keyboard.Multiline, systemInputMode = "none", textAccepted = true });
        }

        await SendAsync(pipe, new BrowserPacket { Kind = "keyboard" }, token);
        var current = await ReadUntilAsync(pipe, packet => packet.Kind == "keyboard", token);
        if (!current.Multiline)
        {
            throw new InvalidOperationException("The controller keyboard action lost the selected field.");
        }

        await SendAsync(pipe, new BrowserPacket { Kind = "key", Text = "ShiftTab" }, token);
        await SendAsync(pipe, new BrowserPacket { Kind = "keyboard" }, token);
        current = await ReadUntilAsync(pipe, packet => packet.Kind == "keyboard", token);
        if (current.Text != "first\nsecond" || !current.Replace)
        {
            throw new InvalidOperationException("Reopening the keyboard lost the existing field value.");
        }

        await SendAsync(pipe, new BrowserPacket { Kind = "text", Text = "edited", Replace = true, NavigationId = current.NavigationId }, token);
        await SendAsync(pipe, new BrowserPacket { Kind = "capture" }, token);
        await ReadUntilAsync(pipe, packet => packet.Kind == "capture", token);
        if ((await EvaluateAsync(socket, "document.getElementById('multiline').value", token)).GetString() != "edited")
        {
            throw new InvalidOperationException("Editing existing text appended instead of replacing the field.");
        }

        _results.Add(new { phase = "edit-existing-field", controllerKeyboard = true, replaced = true });
    }

    private async Task<JsonElement> EvaluateAsync(ClientWebSocket socket, string expression, CancellationToken token)
    {
        var result = await CdpAsync(socket, "Runtime.evaluate", new { expression, returnByValue = true }, token);
        return result.GetProperty("result").GetProperty("value").Clone();
    }

    private async Task<JsonElement> CdpAsync(ClientWebSocket socket, string method, object parameters, CancellationToken token)
    {
        var id = ++_commandId;
        var request = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, method, @params = parameters }));
        await socket.SendAsync(request, WebSocketMessageType.Text, endOfMessage: true, token);
        while (true)
        {
            using MemoryStream message = new();
            var buffer = new byte[8192];
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                message.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            using var response = JsonDocument.Parse(message.ToArray());
            var root = response.RootElement;
            if (!root.TryGetProperty("id", out var received) || received.GetInt32() != id)
            {
                if (root.TryGetProperty("method", out var notification)
                    && notification.GetString() is "Runtime.executionContextCreated" or "Runtime.exceptionThrown")
                {
                    _results.Add(new { phase = "runtime-diagnostic", details = root.Clone() });
                }

                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(method + ": " + error);
            }

            return root.GetProperty("result").Clone();
        }
    }
}
