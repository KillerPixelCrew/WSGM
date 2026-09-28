using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SteamReceiver;

/// <summary>
///     Prints what Steam Input delivers to a game. Add the executable to Steam as a non-Steam game and
///     launch it from Steam: the virtual XInput pad Steam creates for the shortcut's layout, and any keys
///     the layout injects, show up here as they change.
/// </summary>
internal static class Program
{
    private const int ErrorDeviceNotConnected = 1167;
    private static readonly State[] Last = new State[4];
    private static readonly bool[] Connected = new bool[4];
    private static readonly byte[] LeftMax = new byte[4];
    private static readonly byte[] RightMax = new byte[4];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

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

    private static readonly (ushort Mask, string Name)[] ButtonNames =
    [
        (0x0001, "Up"), (0x0002, "Down"), (0x0004, "Left"), (0x0008, "Right"),
        (0x0010, "Start"), (0x0020, "Back"), (0x0040, "LS"), (0x0080, "RS"),
        (0x0100, "LB"), (0x0200, "RB"), (0x0400, "Guide"), (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y")
    ];

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("SteamReceiver: shows what Steam Input hands this process. Esc quits.");
        foreach (var name in new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId", "SDL_GAMECONTROLLER_IGNORE_DEVICES", "EnableConfiguratorSupport" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            Console.WriteLine($"  {name} = {(value is null ? "(not set)" : value)}");
        }

        Console.WriteLine(Environment.GetEnvironmentVariable("SteamAppId") is null
            ? "  Not launched by Steam: only physical pads and the raw virtual Deck will show here."
            : "  Launched by Steam: the pad below is Steam Input's virtual controller for this shortcut.");
        Console.WriteLine("  columns: time  slot  LT RT (0-255)  buttons  | sticks when they move");

        while (true)
        {
            while (!Console.IsInputRedirected && Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape)
                {
                    return 0;
                }

                Console.WriteLine($"{Stamp()}  key   {key.Key}{(key.KeyChar is >= ' ' and < (char)127 ? $" '{key.KeyChar}'" : "")}{(key.Modifiers == 0 ? "" : $" +{key.Modifiers}")}");
            }

            for (uint slot = 0; slot < 4; slot++)
            {
                Poll(slot);
            }

            Thread.Sleep(4);
        }
    }

    private static void Poll(uint slot)
    {
        var result = XInputGetState(slot, out var state);
        if (result == ErrorDeviceNotConnected)
        {
            if (Connected[slot])
            {
                Connected[slot] = false;
                Console.WriteLine($"{Stamp()}  slot{slot} disconnected");
            }

            return;
        }

        if (result != 0)
        {
            return;
        }

        if (!Connected[slot])
        {
            Connected[slot] = true;
            Console.WriteLine($"{Stamp()}  slot{slot} connected");
            Last[slot] = state;
            Print(slot, state, sticks: true);
            return;
        }

        var previous = Last[slot];
        if (state.PacketNumber == previous.PacketNumber)
        {
            return;
        }

        Last[slot] = state;
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

        if (g.LeftTrigger > LeftMax[slot])
        {
            LeftMax[slot] = g.LeftTrigger;
        }

        if (g.RightTrigger > RightMax[slot])
        {
            RightMax[slot] = g.RightTrigger;
        }

        Print(slot, state, sticksMoved);
        if (buttonsChanged)
        {
            foreach (var (mask, name) in ButtonNames)
            {
                var was = (p.Buttons & mask) != 0;
                var now = (g.Buttons & mask) != 0;
                if (was != now)
                {
                    Console.WriteLine($"{Stamp()}  slot{slot}   {name} {(now ? "DOWN" : "up")}");
                }
            }
        }
    }

    private static void Print(uint slot, State state, bool sticks)
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

        var line = $"{Stamp()}  slot{slot} LT {g.LeftTrigger,3} RT {g.RightTrigger,3} (max {LeftMax[slot]}/{RightMax[slot]})  [{string.Join(" ", down)}]";
        if (sticks)
        {
            line += $" | L {g.ThumbLX},{g.ThumbLY} R {g.ThumbRX},{g.ThumbRY}";
        }

        Console.WriteLine(line);
    }

    private static string Stamp()
    {
        return Clock.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(9);
    }
}
