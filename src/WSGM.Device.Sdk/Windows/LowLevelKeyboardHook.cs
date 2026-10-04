using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Sdk.Windows;

/// <summary>One key event as a low-level keyboard hook sees it.</summary>
/// <param name="VirtualKey">The virtual-key code.</param>
/// <param name="Down">True for a key-down or system key-down, false for a key-up.</param>
/// <param name="Injected">Whether the event was synthesized, by SendInput or another injector.</param>
/// <param name="ExtraInfo">The event's extra information, which an injector can use as a marker.</param>
public readonly record struct KeyboardHookEvent(uint VirtualKey, bool Down, bool Injected, nuint ExtraInfo);

/// <summary>Decides one key event inside the hook.</summary>
/// <param name="key">The event.</param>
/// <returns>True to swallow the event, false to pass it on.</returns>
/// <remarks>Runs on the hook thread inside Windows' input dispatch: no waiting, no I/O, no logging.</remarks>
public delegate bool KeyboardHookHandler(in KeyboardHookEvent key);

/// <summary>A <c>WH_KEYBOARD_LL</c> hook on its own thread with its own message loop.</summary>
/// <remarks>
///     A handheld's OEM buttons often arrive as keyboard keys, and Windows opens keyboards exclusively,
///     so a hook is how a package claims them, as HC's keyboard chords are. The hook thread installs,
///     pumps messages, and unhooks in its own <c>finally</c>; a failed install or an unexpected end of the
///     loop is reported, never silent, and a later start begins cleanly.
/// </remarks>
public sealed partial class LowLevelKeyboardHook : IAsyncDisposable
{
    private const int KeyboardLowLevel = 13;
    private const uint Quit = 0x0012;
    private const nuint KeyDown = 0x0100;
    private const nuint KeyUp = 0x0101;
    private const nuint SystemKeyDown = 0x0104;
    private const nuint SystemKeyUp = 0x0105;
    private const uint InjectedFlag = 0x10;

    private readonly Lock _gate = new();
    private readonly HookProcedure _procedure;
    private readonly string _threadName;
    private Action<Exception>? _fault;
    private KeyboardHookHandler? _handler;
    private nint _hook;
    private int _stopping;
    private Thread? _thread;
    private uint _threadId;

    /// <summary>Creates a hook that is not installed yet.</summary>
    /// <param name="threadName">The hook thread's name, for diagnostics.</param>
    public LowLevelKeyboardHook(string threadName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadName);
        _threadName = threadName;
        _procedure = Callback;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Installs the hook on its own thread.</summary>
    /// <param name="handler">Decides each key event.</param>
    /// <param name="fault">Told when the hook's message loop ends without a stop request.</param>
    /// <param name="cancellationToken">Cancels waiting for the install.</param>
    /// <param name="prepare">Runs on the hook thread just before the install, for reading key state.</param>
    /// <param name="stopped">Runs on the hook thread after it unhooked, however it ended.</param>
    /// <returns>Whether the hook is installed.</returns>
    public async ValueTask<bool> StartAsync(
        KeyboardHookHandler handler,
        Action<Exception> fault,
        CancellationToken cancellationToken,
        Action? prepare = null,
        Action? stopped = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(fault);
        TaskCompletionSource<bool> started;
        lock (_gate)
        {
            if (_thread is not null)
            {
                return _hook != 0;
            }

            started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _handler = handler;
            _fault = fault;
            Volatile.Write(ref _stopping, 0);
            _thread = new Thread(() => Run(started, prepare, stopped)) { IsBackground = true, Name = _threadName };
            _thread.Start();
        }

        try
        {
            return await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Removes the hook and ends its thread.</summary>
    /// <param name="cancellationToken">Cancels waiting for the thread.</param>
    /// <returns>A task completing once the thread ended.</returns>
    /// <exception cref="TimeoutException">The hook thread did not end within a second.</exception>
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Thread? thread;
        uint threadId;
        lock (_gate)
        {
            thread = _thread;
            threadId = _threadId;
        }

        if (thread is null)
        {
            return;
        }

        Volatile.Write(ref _stopping, 1);
        if (threadId != 0)
        {
            _ = PostThreadMessage(threadId, Quit, 0, 0);
        }

        var joined = await Task.Run(() => thread.Join(TimeSpan.FromSeconds(1)), CancellationToken.None)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!joined)
        {
            throw new TimeoutException($"The {_threadName} thread did not stop within one second.");
        }
    }

    /// <summary>Whether a key is down right now, from the asynchronous key state.</summary>
    /// <param name="virtualKey">The virtual-key code.</param>
    /// <returns>True while the key is held.</returns>
    public static bool IsKeyDown(uint virtualKey)
    {
        return (GetAsyncKeyState(checked((int)virtualKey)) & 0x8000) != 0;
    }

    private void Run(TaskCompletionSource<bool> started, Action? prepare, Action? stopped)
    {
        lock (_gate)
        {
            _threadId = GetCurrentThreadId();
        }

        try
        {
            prepare?.Invoke();
            _hook = SetWindowsHookEx(KeyboardLowLevel, _procedure, 0, 0);
            if (_hook == 0)
            {
                started.TrySetResult(false);
                return;
            }

            started.TrySetResult(true);
            var result = 0;
            while (Volatile.Read(ref _stopping) == 0 && (result = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }

            if (Volatile.Read(ref _stopping) == 0)
            {
                ReportFault(new InvalidOperationException(result < 0
                    ? $"The {_threadName} message loop failed with Win32 {Marshal.GetLastPInvokeError()}."
                    : $"The {_threadName} thread ended without a stop request."));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            started.TrySetResult(false);
            ReportFault(ex);
        }
        finally
        {
            if (_hook != 0)
            {
                _ = UnhookWindowsHookEx(_hook);
                _hook = 0;
            }

            try
            {
                stopped?.Invoke();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportFault(ex);
            }
            finally
            {
                lock (_gate)
                {
                    _thread = null;
                    _threadId = 0;
                    _handler = null;
                    _fault = null;
                }
            }
        }
    }

    private unsafe nint Callback(int code, nuint message, nint data)
    {
        if (code >= 0 && message is KeyDown or KeyUp or SystemKeyDown or SystemKeyUp
                      && Volatile.Read(ref _handler) is { } handler)
        {
            try
            {
                var raw = *(KeyboardHookData*)data;
                KeyboardHookEvent key = new(raw.VirtualKey, message is KeyDown or SystemKeyDown,
                    (raw.Flags & InjectedFlag) != 0, raw.ExtraInfo);
                if (handler(in key))
                {
                    return 1;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Retire this handler once; an exception must never cross Windows' callback boundary.
                Volatile.Write(ref _handler, null);
                if (Interlocked.Exchange(ref _stopping, 1) == 0)
                {
                    ReportFault(ex);
                    _ = PostThreadMessage(_threadId, Quit, 0, 0);
                }
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private void ReportFault(Exception exception)
    {
        try
        {
            Volatile.Read(ref _fault)?.Invoke(exception);
        }
        catch (Exception callback) when (callback is not OutOfMemoryException)
        {
            PluginTrace.Failure("keyboard", $"{_threadName} fault reporting failed", callback);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, HookProcedure procedure, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    private delegate nint HookProcedure(int code, nuint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }
}
