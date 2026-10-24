using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Browser;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Session-owned lazy browser lifetime. Construction never creates an engine or a process.</summary>
internal sealed class BrowserSession : IDisposable
{
    private readonly UserDataContext _context;
    private readonly Func<BrowserOptions, BrowserOptions> _persist;
    private readonly Func<BrowserGameContext?> _currentGame;
    private readonly Action? _refreshGame;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private BrowserOptions _options;
    private NamedPipeServerStream? _pipe;
    private CancellationTokenSource? _creation;
    private Process? _helper;
    private Process? _unreleasedHelper;
    private Task _cleanup = Task.CompletedTask;
    private Task _startup = Task.CompletedTask;
    private nint _viewport;
    private nint _child;
    private int _width = 1;
    private int _height = 1;
    private int _generation;
    private bool _active;
    private bool _covered;
    private bool _disposed;
    private bool _ready;
    private bool? _sentVisible;
    private string? _pendingNavigation;

    internal BrowserSession(UserDataContext context, BrowserOptions options, Func<BrowserOptions, BrowserOptions> persist,
        Func<BrowserGameContext?> currentGame, Action? refreshGame = null)
    {
        _context = context;
        _options = options.Normalize();
        _persist = persist;
        _currentGame = currentGame;
        _refreshGame = refreshGame;
    }

    internal event Action? Changed;
    internal event Action? ChromeFocusRequested;
    internal event Action? KeyboardRequested;
    internal BrowserLifecycle Lifecycle { get; private set; }
    internal string Address { get; private set; } = BrowserNavigation.Home;
    internal string Status { get; private set; } = "Enter an address or search for a guide.";
    internal bool Loading { get; private set; }
    internal bool CanGoBack { get; private set; }
    internal bool CanGoForward { get; private set; }
    internal bool ViewportFocused { get; private set; }
    internal BrowserPacket TextField { get; private set; } = new();
    internal BrowserOptions Options => _options.Normalize();
    internal BrowserGameContext? CurrentGame => _currentGame();
    internal int? HelperProcessId => _helper?.Id;
    internal Task ShutdownCompletion => _cleanup;

    internal void ApplyOptions(BrowserOptions options)
    {
        _options = options.Normalize();
        Send(new BrowserPacket { Kind = "zoom", Zoom = _options.ZoomFactor });
        if (!_active && _options.MemoryBehavior == BrowserMemoryBehavior.UnloadWhenClosed)
        {
            Destroy();
        }

        Notify();
    }

    internal void SetMemoryBehavior(BrowserMemoryBehavior behavior)
    {
        try
        {
            var next = _options with { MemoryBehavior = behavior };
            ApplyOptions(_persist(next.Normalize()));
        }
        catch (Exception ex)
        {
            Status = "Could not save browser preference: " + ex.Message;
            Notify();
        }
    }

    internal void SetZoom(double value)
    {
        try
        {
            var next = (_options with { ZoomFactor = value }).Normalize();
            ApplyOptions(_persist(next));
        }
        catch (Exception ex)
        {
            Status = "Could not save browser zoom: " + ex.Message;
            Notify();
        }
    }

    internal void Open()
    {
        if (_disposed || !PriorHelperReleased())
        {
            return;
        }

        _active = true;
        _refreshGame?.Invoke();
        if (_ready)
        {
            UpdateVisibility();
        }
        else if (_creation is null && _viewport != 0)
        {
            _creation = new CancellationTokenSource();
            var generation = ++_generation;
            Lifecycle = BrowserLifecycle.Creating;
            Status = "Starting browser…";
            Notify();
            _startup = CreateAsync(generation, _creation.Token, _cleanup);
        }
    }

    internal void Close()
    {
        _active = false;
        ViewportFocused = false;
        if (_child != 0)
        {
            BrowserNative.ShowWindow(_child, 0);
        }

        // An unfinished create has no page to preserve. Cancel it in both policies.
        if (!_ready || _options.MemoryBehavior == BrowserMemoryBehavior.UnloadWhenClosed)
        {
            Destroy();
        }
        else
        {
            UpdateVisibility();
        }
    }

    internal void Cover(bool covered)
    {
        if (_covered == covered)
        {
            return;
        }

        _covered = covered;
        if (covered)
        {
            ViewportFocused = false;
        }

        UpdateVisibility();
    }

    internal void AttachViewport(nint viewport)
    {
        _viewport = viewport;
        if (_child != 0 && _helper is { } helper)
        {
            try
            {
                BrowserNative.Attach(_child, viewport, helper.Id);
                Resize(_width, _height);
                UpdateVisibility();
            }
            catch (Exception ex)
            {
                Fail("Browser window unavailable: " + ex.Message);
            }
        }

        if (_active)
        {
            Open();
        }
    }

    internal void DetachViewport(nint viewport)
    {
        if (_viewport != viewport)
        {
            return;
        }

        BrowserNative.Detach(_child);
        _viewport = 0;
        // The selected page owns Open/Close. Losing an HWND during a window or
        // visual-tree transition must not silently close a still-selected browser.
        UpdateVisibility();
    }

    internal void Resize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        if (_child != 0 && _viewport != 0)
        {
            BrowserNative.SetWindowPos(_child, 0, 0, 0, _width, _height, 0x0014);
        }
    }

    internal void NotifyParentPositionChanged() => Send(new BrowserPacket { Kind = "position" });

    internal void Navigate(string input, bool withCurrentGame = false)
    {
        try
        {
            var address = BrowserNavigation.Resolve(input, withCurrentGame ? CurrentGame?.Title : null);
            if (!_ready)
            {
                if (_active && Lifecycle == BrowserLifecycle.Creating)
                {
                    _pendingNavigation = address;
                    Address = address;
                    Status = "Starting browser; your lookup is queued.";
                }
                else
                {
                    Status = "Open or retry the browser before searching.";
                }

                Notify();
                return;
            }

            Address = address;
            Send(new BrowserPacket { Kind = "navigate", Text = address });
            Notify();
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
            Notify();
        }
    }

    internal void NavigateGameSearch(string input, BrowserGameContext expected)
    {
        if (CurrentGame?.AppId != expected.AppId)
        {
            Status = "The running game changed. Open Search current game again.";
            Notify();
            return;
        }

        Navigate(input, withCurrentGame: true);
    }

    internal void GameContextChanged() => Notify();

    internal void Command(string command)
    {
        if (command is "back" or "forward" or "reload" or "stop")
        {
            Send(new BrowserPacket { Kind = command });
        }
    }

    internal void FocusViewport()
    {
        if (!_ready || !_active || _covered)
        {
            return;
        }

        ViewportFocused = true;
        Send(new BrowserPacket { Kind = "focus" });
    }

    internal bool ReturnToChrome()
    {
        if (!ViewportFocused)
        {
            return false;
        }

        ViewportFocused = false;
        ChromeFocusRequested?.Invoke();
        return true;
    }

    internal void InsertText(string text, bool submit = false, bool replace = false, ulong navigationId = 0)
    {
        if (text.Length <= 4096)
        {
            Send(new BrowserPacket { Kind = "text", Text = text, Submit = submit, Replace = replace, NavigationId = navigationId });
        }
    }

    internal void PageKey(string key)
    {
        if (_active && !_covered && _ready && key is "ArrowUp" or "ArrowDown" or "ArrowLeft" or "ArrowRight" or "Tab" or "ShiftTab" or "Enter" or "PageUp" or "PageDown" or "Backspace" or "Delete" or "Home" or "End" or "Space")
        {
            Send(new BrowserPacket { Kind = "key", Text = key });
        }
    }

    internal void RequestKeyboard()
    {
        if (_active && !_covered && _ready && _viewport != 0)
        {
            Send(new BrowserPacket { Kind = "keyboard" });
        }
        else
        {
            Status = "Select a text field on the website first.";
            Notify();
        }
    }

    internal void OpenExternally()
    {
        // The helper invokes the OS browser at medium integrity, only on this explicit chrome action.
        if (BrowserNavigation.IsAllowed(Address) && Address != BrowserNavigation.Home)
        {
            Send(new BrowserPacket { Kind = "external", Text = Address });
        }
    }

    private async Task CreateAsync(int generation, CancellationToken token, Task previousCleanup)
    {
        NamedPipeServerStream? pipe = null;
        Process? helper = null;
        try
        {
            await previousCleanup;
            token.ThrowIfCancellationRequested();
            if (!PriorHelperReleased())
            {
                _creation?.Dispose();
                _creation = null;
                return;
            }
            var executable = Path.Combine(AppContext.BaseDirectory, "Browser", "WSGM.BrowserHost.exe");
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("Browser host is missing. Repair or update WSGM.");
            }

            var pipeName = "WSGM.Browser." + Guid.NewGuid().ToString("N");
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new InvalidOperationException("Current Windows identity is unavailable.");
            var security = new PipeSecurity();
            // Same-account medium helper must be able to write to an elevated resident's pipe.
            // A medium mandatory label avoids relying on an elevated token's default object label.
            security.SetSecurityDescriptorSddlForm("D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;" + sid + ")");
            pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, BrowserWire.MaximumPacketBytes, BrowserWire.MaximumPacketBytes, security,
                HandleInheritability.None, PipeAccessRights.TakeOwnership);
            BrowserNative.SetMediumPipeLabel(pipe.SafePipeHandle);
            using var parent = Process.GetCurrentProcess();
            var arguments = pipeName + " " + Environment.ProcessId + " " + parent.StartTime.ToUniversalTime().Ticks
                            + " \"" + Path.Combine(_context.Root, "Browser") + "\"";
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(25));
            var connection = pipe.WaitForConnectionAsync(budget.Token);
            if (ElevationCheck.IsCurrentProcessElevated() != false)
            {
                var launch = await UnelevatedLauncher.TryStartViaScheduledTaskAsync(_context, executable, arguments,
                    DateTimeOffset.UtcNow.AddSeconds(20), Path.GetDirectoryName(executable), budget.Token);
                if (launch == ScheduledTaskLaunchDisposition.NotDispatched)
                {
                    throw new InvalidOperationException("Windows could not start the browser without administrator rights.");
                }
            }
            else
            {
                helper = Process.Start(new ProcessStartInfo(executable, arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!
                }) ?? throw new InvalidOperationException("The browser helper did not start.");
            }

            await connection;
            var hello = await BrowserWire.ReadAsync(pipe, budget.Token);
            if (hello.Kind != "hello" || !BrowserNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId)
                || hello.ProcessId != clientId || !BrowserNative.IsMediumIntegrity(hello.ProcessId))
            {
                throw new InvalidDataException("The browser helper identity or integrity could not be verified.");
            }

            if (helper is null)
            {
                var candidate = Process.GetProcessById(hello.ProcessId);
                try
                {
                    if (!string.Equals(candidate.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The browser pipe is not connected to the installed helper.");
                    }

                    helper = candidate;
                }
                finally
                {
                    if (!ReferenceEquals(helper, candidate))
                    {
                        // A rejected pipe peer is never acquired as a process we may terminate.
                        candidate.Dispose();
                    }
                }
            }

            if (helper.Id != hello.ProcessId || !string.Equals(helper.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The browser pipe is not connected to the installed helper.");
            }

            token.ThrowIfCancellationRequested();
            if (generation != _generation || !_active || _disposed || _viewport == 0)
            {
                throw new OperationCanceledException(token);
            }

            _child = (nint)hello.Handle;
            BrowserNative.Attach(_child, _viewport, helper.Id);
            Resize(_width, _height);
            await SendAsync(pipe, new BrowserPacket { Kind = "open", Visible = !_covered, Zoom = _options.ZoomFactor }, token);
            token.ThrowIfCancellationRequested();
            if (generation != _generation || !_active || _disposed)
            {
                throw new OperationCanceledException(token);
            }

            _helper = helper;
            _pipe = pipe;
            _ = ReadPacketsAsync(pipe, generation, token);
            // Ownership is now the session's; the read loop must not outlive this generation.
            helper = null;
            pipe = null;
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation && !_disposed)
            {
                Fail("Browser startup timed out. Reopen Browser to retry.");
            }
        }
        catch (Exception ex)
        {
            if (generation == _generation && !_disposed)
            {
                Fail("Browser unavailable: " + ex.Message);
            }
        }
        finally
        {
            pipe?.Dispose();
            if (helper is not null)
            {
                // Creation was canceled before adoption. Pipe closure requests a normal exit first.
                if (!await StopHelperAsync(helper))
                {
                    _unreleasedHelper = helper;
                    PriorHelperReleased();
                }
            }
        }
    }

    private async Task ReadPacketsAsync(NamedPipeServerStream pipe, int generation, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var packet = await BrowserWire.ReadAsync(pipe, token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation == _generation && !_disposed)
                    {
                        Receive(packet);
                    }
                });
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _generation && !_disposed)
                {
                    Fail("The browser disconnected. Reopen Browser to retry.");
                }
            });
        }
    }

    private void Receive(BrowserPacket packet)
    {
        switch (packet.Kind)
        {
            case "state":
                _ready = true;
                Status = packet.Text.Length <= 2048 ? packet.Text : "Browser status unavailable";
                Loading = packet.Loading;
                CanGoBack = packet.CanGoBack;
                CanGoForward = packet.CanGoForward;
                if (_pendingNavigation is { } queued)
                {
                    _pendingNavigation = null;
                    Send(new BrowserPacket { Kind = "navigate", Text = queued });
                }

                UpdateVisibility();
                break;
            case "address" when BrowserNavigation.IsAllowed(packet.Text):
                Address = packet.Text;
                break;
            case "chrome":
                if (_active && !_covered && _viewport != 0)
                {
                    ViewportFocused = true;
                    ReturnToChrome();
                }

                break;
            case "viewport":
                ViewportFocused = _active && !_covered;
                break;
            case "blur":
                ViewportFocused = false;
                break;
            case "field" when _active && !_covered:
                TextField = packet;
                break;
            case "keyboard" when packet.Visible && _active && !_covered && _ready && _viewport != 0:
                TextField = packet;
                KeyboardRequested?.Invoke();
                break;
            case "failed":
                Fail("The browser process stopped. Reopen Browser to retry.");
                return;
        }

        Notify();
    }

    private void UpdateVisibility()
    {
        var visible = _active && !_covered && _viewport != 0;
        if (_child != 0)
        {
            BrowserNative.ShowWindow(_child, visible && _ready ? 8 : 0);
        }

        if (_ready)
        {
            Lifecycle = _active ? BrowserLifecycle.Active : BrowserLifecycle.Retained;
            if (_sentVisible != visible)
            {
                _sentVisible = visible;
                Send(new BrowserPacket { Kind = "visibility", Visible = visible });
            }
        }

        Notify();
    }

    private void Send(BrowserPacket packet)
    {
        if (_pipe is { IsConnected: true } pipe)
        {
            _ = SendGuardedAsync(pipe, packet, _generation);
        }
    }

    private async Task SendGuardedAsync(NamedPipeServerStream pipe, BrowserPacket packet, int generation)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await SendAsync(pipe, packet, budget.Token);
        }
        catch (Exception)
        {
            if (generation == _generation && !_disposed)
            {
                Fail("Browser did not respond. Reopen Browser to retry.");
            }
        }
    }

    private async Task SendAsync(Stream pipe, BrowserPacket packet, CancellationToken token)
    {
        await _writes.WaitAsync(token);
        try
        {
            await BrowserWire.WriteAsync(pipe, packet, token);
        }
        finally
        {
            _writes.Release();
        }
    }

    private void Fail(string message)
    {
        Destroy();
        Lifecycle = BrowserLifecycle.Failed;
        Status = message;
        Notify();
    }

    private void Destroy()
    {
        _generation++;
        _creation?.Cancel();
        _creation?.Dispose();
        _creation = null;
        _ready = false;
        _pendingNavigation = null;
        _sentVisible = null;
        Loading = false;
        CanGoBack = false;
        CanGoForward = false;
        ViewportFocused = false;
        BrowserNative.Detach(_child);
        _child = 0;
        var pipe = _pipe;
        _pipe = null;
        var helper = _helper ?? _unreleasedHelper;
        _helper = null;
        _unreleasedHelper = null;
        if (pipe is null && helper is null && _startup.IsCompleted)
        {
            if (_cleanup.IsCompleted)
            {
                Lifecycle = BrowserLifecycle.NotCreated;
            }

            Notify();
            return;
        }

        Lifecycle = BrowserLifecycle.Destroying;
        Address = BrowserNavigation.Home;
        var generation = _generation;
        _cleanup = ReleaseAsync(pipe, helper, generation, _startup);
        Notify();
    }

    private async Task ReleaseAsync(NamedPipeServerStream? pipe, Process? helper, int generation, Task startup)
    {
        if (pipe is not null)
        {
            try
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SendAsync(pipe, new BrowserPacket { Kind = "close" }, budget.Token);
            }
            catch (Exception)
            {
            }
            finally
            {
                pipe.Dispose();
            }
        }

        if (helper is not null)
        {
            if (!await StopHelperAsync(helper))
            {
                _unreleasedHelper = helper;
                PriorHelperReleased();
            }
        }

        await startup;

        if (generation == _generation && Lifecycle == BrowserLifecycle.Destroying)
        {
            Lifecycle = BrowserLifecycle.NotCreated;
            Status = "Browser unloaded. Site cookies and storage remain in the WSGM profile.";
            Notify();
        }
    }

    private bool PriorHelperReleased()
    {
        if (_unreleasedHelper is null)
        {
            return true;
        }

        try
        {
            if (_unreleasedHelper.HasExited)
            {
                _unreleasedHelper.Dispose();
                _unreleasedHelper = null;
                return true;
            }
        }
        catch (Exception)
        {
        }

        Lifecycle = BrowserLifecycle.Failed;
        Status = "The previous browser host has not exited. Close Browser again before retrying.";
        Notify();
        return false;
    }

    private static async Task<bool> StopHelperAsync(Process helper)
    {
        var released = false;
        try
        {
            try
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await helper.WaitForExitAsync(budget.Token);
            }
            catch (OperationCanceledException)
            {
                // Only this validated helper is terminated after graceful pipe shutdown failed.
                // Never enumerate or terminate unrelated Edge/WebView2/Steam processes.
                helper.Kill();
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }

            released = helper.HasExited;
            return released;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (released)
            {
                helper.Dispose();
            }
        }
    }

    private void Notify() => Changed?.Invoke();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _active = false;
        Destroy();
        Changed = null;
        ChromeFocusRequested = null;
        KeyboardRequested = null;
    }
}
