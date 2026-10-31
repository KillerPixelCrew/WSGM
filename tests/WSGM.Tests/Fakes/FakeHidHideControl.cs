using WSGM.Shell;

namespace WSGM.Tests.Fakes;

/// <summary>HidHide's control device in memory: lists, cloak and inverse mode, with injectable errors.</summary>
internal sealed class FakeHidHideControl(
    IEnumerable<string>? applications = null,
    IEnumerable<string>? devices = null,
    bool active = true) : IHidHideControl
{
    internal List<string> Applications { get; } = [.. applications ?? []];

    internal List<string> Devices { get; } = [.. devices ?? []];

    internal bool Active { get; set; } = active;

    internal bool Inverse { get; init; }

    /// <summary>A Win32 error every read returns; zero reads normally.</summary>
    internal int ReadError { get; init; }

    /// <summary>A Win32 error every write returns; zero writes normally.</summary>
    internal int WriteError { get; set; }

    internal int Reads { get; private set; }

    internal List<string> Calls { get; } = [];

    /// <summary>List and cloak writes attempted, including refused ones.</summary>
    internal int ListWrites { get; private set; }

    public HidHideControlState Read()
    {
        Calls.Add("read");
        Reads++;
        return ReadError != 0
            ? new HidHideControlState(false, ReadError, false, false, [], [])
            : new HidHideControlState(true, 0, Active, Inverse, [.. Applications], [.. Devices]);
    }

    public int Write(HidHideEntryKind entryKind, IReadOnlyList<string> entries)
    {
        ListWrites++;
        if (WriteError != 0)
        {
            return WriteError;
        }

        var list = entryKind is HidHideEntryKind.Application ? Applications : Devices;
        list.Clear();
        list.AddRange(entries);
        return 0;
    }

    public int WriteActive(bool active)
    {
        if (HidHideControlState.IsNotInstalled(ReadError))
        {
            return ReadError;
        }

        Calls.Add($"cloak:{active}");
        ListWrites++;
        if (WriteError != 0)
        {
            return WriteError;
        }

        Active = active;
        return 0;
    }
}
