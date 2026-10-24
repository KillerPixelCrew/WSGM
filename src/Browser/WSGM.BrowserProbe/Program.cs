using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using WSGM.Browser;
using WSGM.Interop;

namespace WSGM.BrowserProbe;

/// <summary>Bounded isolated spike; never launched by WSGM and never included in shipping output.</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var window = new ProbeWindow(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]),
            keyboardOnly: args.Length == 3 && args[2] == "keyboard");
        Application.Run(window);
    }
}

internal sealed partial class ProbeWindow : Form
{
    private readonly string _helperPath;
    private readonly string _output;
    private readonly List<object> _results = [];
    private readonly List<object> _failures = [];
    private readonly CancellationTokenSource _budget = new(TimeSpan.FromSeconds(110));
    private readonly List<Process> _ownedHelpers = [];
    private readonly TcpListener _fixture = new(IPAddress.Loopback, 0);
    private readonly System.Windows.Forms.Timer _deadline = new() { Interval = 115000 };

    internal ProbeWindow(string helperPath, string output, bool keyboardOnly = false)
    {
        _helperPath = helperPath;
        _output = output;
        _keyboardOnly = keyboardOnly;
        Text = "WSGM Browser isolated acceptance spike";
        ClientSize = new Size(880, 520);
        StartPosition = FormStartPosition.CenterScreen;
        if (keyboardOnly)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
        }
        ShowInTaskbar = false;
        _deadline.Tick += (_, _) => Close();
        _deadline.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        var token = _budget.Token;
        try
        {
            Directory.CreateDirectory(_output);
            _results.Add(new { phase = "parent", pid = Environment.ProcessId, medium = BrowserNative.IsMediumIntegrity(Environment.ProcessId), dpi = DeviceDpi });
            await SampleAsync("before-first-open", [], token);
            var profile = Path.Combine(_output, "profile", "Browser");
            _fixture.Start();
            _ = ServeFixtureAsync(token);
            var fixtureAddress = "http://127.0.0.1:" + ((IPEndPoint)_fixture.LocalEndpoint).Port + "/browser-acceptance";
            var watch = Stopwatch.StartNew();
            var (pipe, helper, child) = await StartAsync(profile, token);
            await using (pipe)
            {
                _results.Add(new { phase = "helper-before-engine", pid = helper.Id, medium = BrowserNative.IsMediumIntegrity(helper.Id), helperHandshakeMs = watch.Elapsed.TotalMilliseconds });
                await SendAsync(pipe, new BrowserPacket { Kind = "processes" }, token);
                var before = await ReadUntilAsync(pipe, packet => packet.Kind == "processes", token);
                _results.Add(new { phase = "before-engine-pids", pids = before.Text });
                if (before.Text != "[]")
                {
                    throw new InvalidOperationException("A WebView2 process exists before open.");
                }
                BrowserNative.Attach(child, Handle, helper.Id);
                BrowserNative.SetWindowPos(child, 0, 0, 0, ClientSize.Width, ClientSize.Height, 0x14);
                BrowserNative.ShowWindow(child, 8);
                watch.Restart();
                await SendAsync(pipe, new BrowserPacket { Kind = "open", Visible = true }, token);
                var ready = await ReadUntilAsync(pipe, packet => packet.Kind is "failed" || packet.Kind == "state" && !packet.Loading, token);
                if (ready.Kind == "failed")
                {
                    throw new InvalidOperationException(ready.Text);
                }
                _results.Add(new { phase = "first-open", controllerReadyMs = watch.Elapsed.TotalMilliseconds, status = ready.Text });
                if (_keyboardOnly)
                {
                    await CheckKeyboardAsync(pipe, fixtureAddress, token);
                    await SendAsync(pipe, new BrowserPacket { Kind = "close" }, token);
                    await helper.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
                    return;
                }
                var runtime = await ProcessesAsync(pipe, token);
                await SampleAsync("first-open", runtime.Append(helper.Id).ToArray(), token);

                foreach (var address in new[] { "https://example.com/", "https://en.wikipedia.org/wiki/Video_game" })
                {
                    watch.Restart();
                    var page = await NavigateAsync(pipe, address, token);
                    _results.Add(new { phase = "representative-navigation", address, elapsedMs = watch.Elapsed.TotalMilliseconds, status = page.Text });
                    if (page.Text.Contains("could not load", StringComparison.OrdinalIgnoreCase))
                    {
                        _failures.Add(new { phase = "navigation", address, status = page.Text });
                    }
                }

                runtime = await ProcessesAsync(pipe, token);
                await SampleAsync("after-sites", runtime.Append(helper.Id).ToArray(), token);
                await NavigateAsync(pipe, fixtureAddress, token);
                await SendAsync(pipe, new BrowserPacket { Kind = "focus" }, token);
                await SendAsync(pipe, new BrowserPacket { Kind = "text", Text = "retained 220" }, token);
                await SendAsync(pipe, new BrowserPacket { Kind = "key", Text = "Tab" }, token);
                await SendAsync(pipe, new BrowserPacket { Kind = "key", Text = "Enter" }, token);
                var download = await ReadUntilAsync(pipe, packet => packet.Kind == "state" && packet.Text.StartsWith("Downloads are disabled", StringComparison.Ordinal), token);
                _results.Add(new { phase = "download-policy", status = download.Text });
                await SendAsync(pipe, new BrowserPacket { Kind = "key", Text = "Tab" }, token);
                await SendAsync(pipe, new BrowserPacket { Kind = "key", Text = "Enter" }, token);
                // Chromium can reject renderer-originated file links before NavigationStarting.
                // Check the explicit host refusal as well, without expecting an event Chromium suppresses.
                await SendAsync(pipe, new BrowserPacket { Kind = "navigate", Text = "file:///C:/Windows/win.ini" }, token);
                var fileLink = await ReadUntilAsync(pipe, packet => packet.Kind == "state" && packet.Text.StartsWith("Blocked address", StringComparison.Ordinal), token);
                var unchanged = await ReadUntilAsync(pipe, packet => packet.Kind == "address", token);
                if (unchanged.Text != fixtureAddress)
                {
                    throw new InvalidOperationException("A local-file request changed the browser's address.");
                }

                _results.Add(new { phase = "file-scheme-policy", status = fileLink.Text, retainedAddress = unchanged.Text });
                await SendAsync(pipe, new BrowserPacket { Kind = "capture" }, token);
                await ReadUntilAsync(pipe, packet => packet.Kind == "capture", token);
                File.Copy(Path.Combine(profile, "BrowserAcceptance.png"), Path.Combine(_output, "visible.png"), overwrite: true);
                // Exercise embedding resize and a parent move without activating another app.
                ClientSize = new Size(760, 440);
                BrowserNative.SetWindowPos(child, 0, 0, 0, ClientSize.Width, ClientSize.Height, 0x14);
                await SendAsync(pipe, new BrowserPacket { Kind = "position" }, token);
                await SendAsync(pipe, new BrowserPacket { Kind = "capture" }, token);
                await ReadUntilAsync(pipe, packet => packet.Kind == "capture", token);
                File.Copy(Path.Combine(profile, "BrowserAcceptance.png"), Path.Combine(_output, "resized.png"), overwrite: true);

                runtime = await ProcessesAsync(pipe, token);
                BrowserNative.ShowWindow(child, 0);
                await SendAsync(pipe, new BrowserPacket { Kind = "visibility", Visible = false }, token);
                var hidden = await ReadUntilAsync(pipe, packet => packet.Kind == "state" && packet.Text.StartsWith("Retained", StringComparison.Ordinal), token);
                _results.Add(new { phase = "keep-alive-hide", status = hidden.Text });
                await SampleAsync("retained-hidden", runtime.Append(helper.Id).ToArray(), token);
                watch.Restart();
                BrowserNative.ShowWindow(child, 8);
                await SendAsync(pipe, new BrowserPacket { Kind = "visibility", Visible = true }, token);
                await SendAsync(pipe, new BrowserPacket { Kind = "processes" }, token);
                var reopen = await ReadUntilAsync(pipe, packet => packet.Kind == "processes", token);
                _results.Add(new { phase = "keep-alive-reopen", commandRoundTripMs = watch.Elapsed.TotalMilliseconds, pids = reopen.Text });
                await SendAsync(pipe, new BrowserPacket { Kind = "capture" }, token);
                await ReadUntilAsync(pipe, packet => packet.Kind == "capture", token);
                File.Copy(Path.Combine(profile, "BrowserAcceptance.png"), Path.Combine(_output, "retained-reopened.png"), overwrite: true);
                BrowserNative.ShowWindow(child, 0);
                BrowserNative.Detach(child);
                watch.Restart();
                await SendAsync(pipe, new BrowserPacket { Kind = "close" }, token);
                await helper.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(6), token);
                await WaitReleasedAsync(runtime, token);
                _results.Add(new { phase = "unload", helperExited = helper.HasExited, runtimeProcessesExited = runtime.All(HasExited), releaseMs = watch.Elapsed.TotalMilliseconds });
                await SampleAsync("after-unload", runtime, token);
            }

            // New helper/profile group after unloading, then cancellation while environment is creating.
            watch.Restart();
            var (secondPipe, secondHelper, secondChild) = await StartAsync(profile, token);
            await using (secondPipe)
            {
                BrowserNative.Attach(secondChild, Handle, secondHelper.Id);
                BrowserNative.SetWindowPos(secondChild, 0, 0, 0, ClientSize.Width, ClientSize.Height, 0x14);
                BrowserNative.ShowWindow(secondChild, 8);
                await SendAsync(secondPipe, new BrowserPacket { Kind = "open", Visible = true }, token);
                await ReadUntilAsync(secondPipe, packet => packet.Kind == "state" && !packet.Loading, token);
                _results.Add(new { phase = "unload-reopen", controllerReadyMs = watch.Elapsed.TotalMilliseconds });
                await NavigateAsync(secondPipe, fixtureAddress, token);
                await SendAsync(secondPipe, new BrowserPacket { Kind = "capture" }, token);
                await ReadUntilAsync(secondPipe, packet => packet.Kind == "capture", token);
                File.Copy(Path.Combine(profile, "BrowserAcceptance.png"), Path.Combine(_output, "unload-reopened.png"), overwrite: true);
                var secondRuntime = await ProcessesAsync(secondPipe, token);
                await SendAsync(secondPipe, new BrowserPacket { Kind = "close" }, token);
                await secondHelper.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
                await WaitReleasedAsync(secondRuntime, token);
            }

            var (cancelPipe, cancelHelper, _) = await StartAsync(profile, token);
            await using (cancelPipe)
            {
                await SendAsync(cancelPipe, new BrowserPacket { Kind = "open", Visible = false }, token);
                await SendAsync(cancelPipe, new BrowserPacket { Kind = "close" }, token);
                await cancelHelper.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
                _results.Add(new { phase = "close-during-create", exited = cancelHelper.HasExited });
            }
        }
        catch (Exception ex)
        {
            _failures.Add(new { phase = "probe", error = ex.ToString() });
        }
        finally
        {
            _fixture.Stop();
            foreach (var helper in _ownedHelpers)
            {
                try
                {
                    if (!helper.HasExited)
                    {
                        helper.Kill();
                    }
                }
                catch (Exception)
                {
                }
                helper.Dispose();
            }

            File.WriteAllText(Path.Combine(_output, "result.json"), JsonSerializer.Serialize(new { results = _results, failures = _failures }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }

    private async Task ServeFixtureAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await _fixture.AcceptTcpClientAsync(token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var request = await reader.ReadLineAsync(token) ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token)))
                {
                }

                var download = request.Contains("/download", StringComparison.Ordinal);
                var body = download ? "browser probe" : """
                    <!doctype html><html><head><meta charset="utf-8"><style>
                    body{background:#171b20;color:#eee;font:22px system-ui;margin:26px}input{font:24px system-ui;width:90%;padding:8px}
                    a{display:block;color:#8dc5ff;margin:20px 0}p{font-size:18px}label{display:block}
                    </style></head><body><h1>Browser retained page</h1><label>Page input<input id="state" autofocus></label>
                    <a href="/download" download="browser-probe.txt">Download must be blocked</a>
                    <a href="file:///C:/Windows/win.ini">Local file must be blocked</a>
                    <p id="visits"></p><p id="bridges"></p><script>
                    const n=Number(localStorage.visits||0)+1;localStorage.visits=String(n);
                    document.querySelector('#visits').textContent='Profile visits: '+n+' (same page = 1; recreated page = 2)';
                    document.querySelector('#bridges').textContent='SteamClient: '+typeof window.SteamClient+'; WSGM: '+typeof window.WSGM+'; web messaging: '+typeof window.chrome?.webview?.postMessage;
                    document.querySelector('#state').focus();
                    </script></body></html>
                    """;
                if (_keyboardOnly)
                {
                    body = KeyboardFixture;
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                var header = "HTTP/1.1 200 OK\r\nContent-Type: " + (download ? "application/octet-stream" : "text/html; charset=utf-8")
                             + (download ? "\r\nContent-Disposition: attachment; filename=browser-probe.txt" : "")
                             + "\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
                await stream.WriteAsync(bytes, token);
            }
        }
        catch (Exception) when (token.IsCancellationRequested || !_fixture.Server.IsBound)
        {
        }
    }

    private static async Task<BrowserPacket> NavigateAsync(Stream pipe, string address, CancellationToken token)
    {
        await SendAsync(pipe, new BrowserPacket { Kind = "navigate", Text = address }, token);
        var navigation = await ReadUntilAsync(pipe, packet => packet.Kind == "address" && packet.Text == address, token);
        return await ReadUntilAsync(pipe, packet => packet.Kind == "state" && !packet.Loading && packet.NavigationId == navigation.NavigationId, token);
    }

    private async Task<(NamedPipeServerStream Pipe, Process Helper, nint Child)> StartAsync(string profile, CancellationToken token)
    {
        var name = "WSGM.Browser." + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm("D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;" + identity.User!.Value + ")");
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 32768, 32768, security, HandleInheritability.None, PipeAccessRights.TakeOwnership);
        BrowserNative.SetMediumPipeLabel(pipe.SafePipeHandle);
        using var parent = Process.GetCurrentProcess();
        var arguments = name + " " + Environment.ProcessId + " " + parent.StartTime.ToUniversalTime().Ticks + " \"" + profile + "\"";
        try
        {
            if (BrowserNative.IsMediumIntegrity(Environment.ProcessId))
            {
                var start = new ProcessStartInfo(_helperPath, arguments) { UseShellExecute = false, CreateNoWindow = true };
                if (_keyboardOnly)
                {
                    using var reservation = new TcpListener(IPAddress.Loopback, 0);
                    reservation.Start();
                    _keyboardDebugPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
                    start.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = "--remote-debugging-port=" + _keyboardDebugPort;
                }

                var direct = Process.Start(start)!;
                _ownedHelpers.Add(direct);
            }
            else
            {
                await LaunchMediumAsync(arguments, token);
            }

            await pipe.WaitForConnectionAsync(token).WaitAsync(TimeSpan.FromSeconds(20), token);
            var hello = await ReadUntilAsync(pipe, packet => packet.Kind == "hello", token);
            if (!BrowserNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != hello.ProcessId || !BrowserNative.IsMediumIntegrity(hello.ProcessId))
            {
                throw new InvalidOperationException("Helper was not a verified medium-integrity pipe peer.");
            }
            var helper = Process.GetProcessById(hello.ProcessId);
            _ownedHelpers.Add(helper);
            return (pipe, helper, (nint)hello.Handle);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private async Task LaunchMediumAsync(string arguments, CancellationToken token)
    {
        var name = "WSGM_BrowserSpike_" + Guid.NewGuid().ToString("N");
        var xml = Path.Combine(_output, name + ".xml");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        using var identity = WindowsIdentity.GetCurrent();
        var task = new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "Browser"),
                new XElement(ns + "UserId", identity.User!.Value), new XElement(ns + "LogonType", "InteractiveToken"), new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings", new XElement(ns + "ExecutionTimeLimit", "PT2M"), new XElement(ns + "DisallowStartIfOnBatteries", "false"), new XElement(ns + "StopIfGoingOnBatteries", "false")),
            new XElement(ns + "Actions", new XAttribute("Context", "Browser"), new XElement(ns + "Exec", new XElement(ns + "Command", _helperPath), new XElement(ns + "Arguments", arguments))));
        File.WriteAllText(xml, new XDocument(new XDeclaration("1.0", "utf-16", null), task).ToString(), Encoding.Unicode);
        try
        {
            await SchtasksAsync(["/Create", "/TN", name, "/XML", xml, "/F"], token);
            await SchtasksAsync(["/Run", "/TN", name], token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SchtasksAsync(["/Delete", "/TN", name, "/F"], cleanup.Token);
            File.Delete(xml);
        }
    }

    private static async Task SchtasksAsync(string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await output + await error);
        }
    }

    private static Task SendAsync(Stream pipe, BrowserPacket packet, CancellationToken token) => BrowserWire.WriteAsync(pipe, packet, token);

    private static async Task<BrowserPacket> ReadUntilAsync(Stream pipe, Func<BrowserPacket, bool> predicate, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var packet = await BrowserWire.ReadAsync(pipe, timeout.Token);
            if (packet.Kind == "failed")
            {
                throw new InvalidOperationException(packet.Text);
            }

            if (predicate(packet))
            {
                return packet;
            }
        }
    }

    private static async Task<int[]> ProcessesAsync(Stream pipe, CancellationToken token)
    {
        await SendAsync(pipe, new BrowserPacket { Kind = "processes" }, token);
        return JsonSerializer.Deserialize<int[]>((await ReadUntilAsync(pipe, packet => packet.Kind == "processes", token)).Text) ?? [];
    }

    private async Task SampleAsync(string phase, int[] processIds, CancellationToken token)
    {
        var processes = processIds.Append(Environment.ProcessId).Distinct().Where(id => !HasExited(id)).Select(Process.GetProcessById).ToArray();
        try
        {
            var cpu = processes.ToDictionary(process => process.Id, process => process.TotalProcessorTime.TotalMilliseconds);
            var watch = Stopwatch.StartNew();
            await Task.Delay(1500, token);
            var samples = new List<object>();
            foreach (var process in processes)
            {
                if (process.HasExited)
                {
                    continue;
                }
                process.Refresh();
                var memory = new ProcessMemory { Size = (uint)Marshal.SizeOf<ProcessMemory>() };
                var known = GetProcessMemoryInfo(process.Handle, ref memory, memory.Size);
                samples.Add(new
                {
                    pid = process.Id, name = process.ProcessName, workingSet = process.WorkingSet64,
                    privateBytes = process.PrivateMemorySize64, privateWorkingSet = known ? (ulong?)memory.PrivateWorkingSet : null,
                    cpuPercentOfMachine = 100 * (process.TotalProcessorTime.TotalMilliseconds - cpu[process.Id]) / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount
                });
            }

            _results.Add(new { phase, processes = samples });
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static async Task WaitReleasedAsync(int[] processes, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (processes.Any(id => !HasExited(id)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, token);
        }

        if (processes.Any(id => !HasExited(id)))
        {
            throw new InvalidOperationException("WebView2 processes remain after helper unload.");
        }
    }

    private static bool HasExited(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.HasExited; } catch (ArgumentException) { return true; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemory
    {
        internal uint Size;
        internal uint PageFaults;
        internal nuint PeakWorkingSet;
        internal nuint WorkingSet;
        internal nuint PeakPagedPool;
        internal nuint PagedPool;
        internal nuint PeakNonPagedPool;
        internal nuint NonPagedPool;
        internal nuint PageFile;
        internal nuint PeakPageFile;
        internal nuint PrivateUsage;
        internal nuint PrivateWorkingSet;
        internal nuint SharedCommit;
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemory counters, uint size);
}
