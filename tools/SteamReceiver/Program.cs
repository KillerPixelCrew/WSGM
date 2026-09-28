using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SteamReceiver;

/// <summary>
///     Shows what Steam Input delivers to a game. Add the executable to Steam as a non-Steam game and
///     launch it from Steam: the virtual XInput pad Steam creates for the shortcut's layout, and any keys
///     the layout injects, appear here as they change. It is a window of its own, because Steam Input
///     applies a shortcut's layout to the process that owns the foreground window, and a console
///     program's window belongs to Windows Terminal or conhost, not to the program.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new ReceiverForm());
    }
}

internal sealed class ReceiverForm : Form
{
    private const int ErrorDeviceNotConnected = 1167;

    private static readonly (ushort Mask, string Name)[] ButtonNames =
    [
        (0x0001, "Up"), (0x0002, "Down"), (0x0004, "Left"), (0x0008, "Right"),
        (0x0010, "Start"), (0x0020, "Back"), (0x0040, "LS"), (0x0080, "RS"),
        (0x0100, "LB"), (0x0200, "RB"), (0x0400, "Guide"), (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y")
    ];

    private readonly TextBox _log;
    private readonly StreamWriter? _file;
    private readonly State[] _last = new State[4];
    private readonly bool[] _connected = new bool[4];
    private readonly byte[] _leftMax = new byte[4];
    private readonly byte[] _rightMax = new byte[4];
    private volatile bool _closing;

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(uint userIndex, out State state);

    public ReceiverForm()
    {
        Text = "SteamReceiver";
        Width = 1100;
        Height = 700;
        KeyPreview = true;
        var copy = new Button { Text = "Copy all", Dock = DockStyle.Top, Height = 32, TabStop = false };
        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10f),
            WordWrap = false,
            TabStop = false
        };
        copy.Click += (_, _) => Clipboard.SetText(_log.Text);
        Controls.Add(_log);
        Controls.Add(copy);
        KeyDown += OnKeyDown;
        FormClosing += (_, _) => _closing = true;

        var logPath = Path.Combine(AppContext.BaseDirectory, "steam-receiver.log");
        try
        {
            _file = new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };
        }
        catch (IOException)
        {
            _file = null;
        }

        Say("SteamReceiver: shows what Steam Input hands this window. Esc closes. Log: " + logPath);
        foreach (var name in new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId", "SDL_GAMECONTROLLER_IGNORE_DEVICES", "EnableConfiguratorSupport" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            Say($"  {name} = {(value is null ? "(not set)" : value.Length > 80 ? value[..80] + "..." : value)}");
        }

        Say(Environment.GetEnvironmentVariable("SteamAppId") is null
            ? "  Not launched by Steam: only physical pads and the raw virtual Deck show here."
            : "  Launched by Steam: the pad below is Steam Input's virtual controller for this shortcut.");
        Say("  columns: local time  slot  LT RT (0-255)  buttons  | sticks when they move");

        var poller = new Thread(PollLoop) { IsBackground = true, Name = "xinput-poll" };
        poller.Start();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            return;
        }

        Say($"key   {e.KeyCode}{(e.Modifiers == Keys.None ? "" : $" +{e.Modifiers}")}");
    }

    private void PollLoop()
    {
        while (!_closing)
        {
            for (uint slot = 0; slot < 4; slot++)
            {
                Poll(slot);
            }

            Thread.Sleep(4);
        }
    }

    private void Poll(uint slot)
    {
        var result = XInputGetState(slot, out var state);
        if (result == ErrorDeviceNotConnected)
        {
            if (_connected[slot])
            {
                _connected[slot] = false;
                Say($"slot{slot} disconnected");
            }

            return;
        }

        if (result != 0)
        {
            return;
        }

        if (!_connected[slot])
        {
            _connected[slot] = true;
            Say($"slot{slot} connected");
            _last[slot] = state;
            Print(slot, state, sticks: true);
            return;
        }

        var previous = _last[slot];
        if (state.PacketNumber == previous.PacketNumber)
        {
            return;
        }

        _last[slot] = state;
        var g = state.Gamepad;
        var p = previous.Gamepad;
        var buttonsChanged = g.Buttons != p.Buttons;
        var triggersChanged = g.LeftTrigger != p.LeftTrigger || g.RightTrigger != p.RightTrigger;
        var sticksMoved = Math.Abs(g.ThumbLX - p.ThumbLX) > 2000 || Math.Abs(g.ThumbLY - p.ThumbLY) > 2000
                          || Math.Abs(g.ThumbRX - p.ThumbRX) > 2000 || Math.Abs(g.ThumbRY - p.ThumbRY) > 2000;
        if (!buttonsChanged && !triggersChanged && !sticksMoved)
        {
            return;
        }

        if (g.LeftTrigger > _leftMax[slot])
        {
            _leftMax[slot] = g.LeftTrigger;
        }

        if (g.RightTrigger > _rightMax[slot])
        {
            _rightMax[slot] = g.RightTrigger;
        }

        Print(slot, state, sticksMoved);
        if (!buttonsChanged)
        {
            return;
        }

        foreach (var (mask, name) in ButtonNames)
        {
            var was = (p.Buttons & mask) != 0;
            var now = (g.Buttons & mask) != 0;
            if (was != now)
            {
                Say($"slot{slot}   {name} {(now ? "DOWN" : "up")}");
            }
        }
    }

    private void Print(uint slot, State state, bool sticks)
    {
        var g = state.Gamepad;
        List<string> down = [];
        foreach (var (mask, name) in ButtonNames)
        {
            if ((g.Buttons & mask) != 0)
            {
                down.Add(name);
            }
        }

        var line = $"slot{slot} LT {g.LeftTrigger,3} RT {g.RightTrigger,3} (max {_leftMax[slot]}/{_rightMax[slot]})  [{string.Join(" ", down)}]";
        if (sticks)
        {
            line += $" | L {g.ThumbLX},{g.ThumbLY} R {g.ThumbRX},{g.ThumbRY}";
        }

        Say(line);
    }

    /// <summary>Appends one line, stamped with the local wall-clock time, to the window and the log file.</summary>
    private void Say(string text)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {text}";
        _file?.WriteLine(line);
        if (_closing || IsDisposed)
        {
            return;
        }

        if (!IsHandleCreated)
        {
            Append(line);
            return;
        }

        try
        {
            BeginInvoke(() => Append(line));
        }
        catch (InvalidOperationException)
        {
            // The window is going away.
        }
    }

    private void Append(string line)
    {
        if (_closing || IsDisposed)
        {
            return;
        }

        _log.AppendText(line + Environment.NewLine);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            _file?.Dispose();
        }

        base.Dispose(disposing);
    }
}
