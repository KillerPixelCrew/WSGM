using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WSGM.DeviceLab.Core.Inventory;

public static partial class WindowsInventoryCollector
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorDeviceNotConnected = 1167;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const uint RimTypeHid = 2;

    private static readonly string[] RelevantNameFragments =
    [
        "wsgm",
        "msi",
        "center",
        "handheld",
        "hidhide",
        "hidmaestro",
        "steam",
        "rtss",
        "rivatuner",
        "xinput",
        "gamepad",
    ];

    private static IReadOnlyList<GraphicsAdapterInventory> CollectGraphicsAdapters()
    {
        List<GraphicsAdapterInventory> adapters = [];
        try
        {
            using ManagementObjectSearcher searcher = new(
                "root\\CIMV2",
                "SELECT PNPDeviceID, Name, DriverVersion FROM Win32_VideoController");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? instanceId = Text(item, "PNPDeviceID");
                    if (instanceId is null)
                    {
                        continue;
                    }

                    Match identifiers = PciIdentifiers().Match(instanceId);
                    adapters.Add(new GraphicsAdapterInventory
                    {
                        InstanceId = instanceId,
                        Name = Text(item, "Name"),
                        VendorId = identifiers.Success ? identifiers.Groups["ven"].Value.ToUpperInvariant() : null,
                        DeviceId = identifiers.Success ? identifiers.Groups["dev"].Value.ToUpperInvariant() : null,
                        DriverVersion = Text(item, "DriverVersion"),
                    });
                }
            }
        }
        catch (ManagementException)
        {
            // Other inventory lanes remain useful when video-controller WMI is unavailable.
        }

        return [.. adapters.OrderBy(adapter => adapter.InstanceId, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<SerialEndpointInventory> CollectSerialEndpoints()
    {
        List<SerialEndpointInventory> endpoints = [];
        try
        {
            using ManagementObjectSearcher searcher = new(
                "root\\CIMV2",
                "SELECT DeviceID, PNPDeviceID, Name, ProviderType, BaudRate, ByteSize, Parity, StopBits "
                    + "FROM Win32_SerialPort");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string instanceId = Text(item, "PNPDeviceID") ?? Text(item, "DeviceID") ?? "unknown-serial";
                    List<SerialFramingCandidate> candidates = [];
                    uint? baud = UInt32(item, "BaudRate");
                    byte? dataBits = Byte(item, "ByteSize");
                    byte? parity = Byte(item, "Parity");
                    byte? stopBits = Byte(item, "StopBits");
                    if (baud is not null || dataBits is not null || parity is not null || stopBits is not null)
                    {
                        candidates.Add(new SerialFramingCandidate
                        {
                            BaudRate = baud,
                            DataBits = dataBits,
                            Parity = parity,
                            StopBits = stopBits,
                            Source = "Win32_SerialPort-current-driver-state",
                        });
                    }

                    endpoints.Add(new SerialEndpointInventory
                    {
                        InstanceId = instanceId,
                        PortName = Text(item, "DeviceID"),
                        Name = Text(item, "Name"),
                        Manufacturer = Text(item, "ProviderType"),
                        LocationPath = DeviceProperties.ResolveLocationPath(instanceId),
                        Access = InventoryAccess.Available,
                        FramingCandidates = candidates,
                    });
                }
            }
        }
        catch (ManagementException exception)
        {
            if (exception.ErrorCode == ManagementStatus.AccessDenied)
            {
                endpoints.Add(new SerialEndpointInventory
                {
                    InstanceId = "serial-inventory",
                    Access = InventoryAccess.AccessDenied,
                });
            }
        }

        return [.. endpoints.OrderBy(endpoint => endpoint.InstanceId, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<SensorEndpointInventory> CollectSensors()
    {
        List<SensorEndpointInventory> sensors = [];
        try
        {
            using ManagementObjectSearcher searcher = new(
                "root\\CIMV2",
                "SELECT DeviceID, Name, PNPClass, Status FROM Win32_PnPEntity "
                    + "WHERE PNPClass = 'Sensor' OR PNPClass = 'HIDClass'");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? instanceId = Text(item, "DeviceID");
                    string? name = Text(item, "Name");
                    string? deviceClass = Text(item, "PNPClass");
                    bool namedSensor = name?.Contains("sensor", StringComparison.OrdinalIgnoreCase) is true
                        || name?.Contains("accelerometer", StringComparison.OrdinalIgnoreCase) is true
                        || name?.Contains("gyroscope", StringComparison.OrdinalIgnoreCase) is true
                        || name?.Contains("inclinometer", StringComparison.OrdinalIgnoreCase) is true;
                    if (instanceId is null || (!string.Equals(deviceClass, "Sensor", StringComparison.OrdinalIgnoreCase)
                        && !namedSensor))
                    {
                        continue;
                    }

                    sensors.Add(new SensorEndpointInventory
                    {
                        InstanceId = instanceId,
                        Name = name,
                        Kind = deviceClass,
                        AssociationId = DeviceProperties.ResolveParentInstanceId(instanceId),
                        Access = string.Equals(Text(item, "Status"), "OK", StringComparison.OrdinalIgnoreCase)
                            ? InventoryAccess.Available
                            : InventoryAccess.Disconnected,
                    });
                }
            }
        }
        catch (ManagementException exception)
        {
            if (exception.ErrorCode == ManagementStatus.AccessDenied)
            {
                sensors.Add(new SensorEndpointInventory
                {
                    InstanceId = "sensor-inventory",
                    Access = InventoryAccess.AccessDenied,
                });
            }
        }

        return [.. sensors.OrderBy(sensor => sensor.InstanceId, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<InputBackendInventory> CollectInputBackends()
    {
        return
        [
            CollectXInput(),
            CollectDirectInputView(),
            CollectSdlView(),
            CollectRawInput(),
            CollectRawHidView(),
        ];
    }

    private static InputBackendInventory CollectXInput()
    {
        List<InputEndpointInventory> endpoints = [];
        try
        {
            for (uint slot = 0; slot < 4; slot++)
            {
                uint result = XInputGetCapabilities(slot, 0, out XInputCapabilities capabilities);
                if (result == ErrorSuccess)
                {
                    endpoints.Add(new InputEndpointInventory
                    {
                        EndpointId = $"xinput:{slot}",
                        Name = "XInput controller",
                        DeviceType = $"{capabilities.Type:x2}:{capabilities.SubType:x2}",
                        Connected = true,
                    });
                }
                else if (result != ErrorDeviceNotConnected)
                {
                    return new InputBackendInventory
                    {
                        Backend = InputBackendKind.XInput,
                        Access = InventoryAccess.AccessDenied,
                        Endpoints = endpoints,
                        Limitation = $"XInputGetCapabilities returned {result}.",
                    };
                }
            }
        }
        catch (DllNotFoundException)
        {
            return UnavailableBackend(InputBackendKind.XInput, "The system XInput runtime was unavailable.");
        }
        catch (EntryPointNotFoundException)
        {
            return UnavailableBackend(InputBackendKind.XInput, "The system XInput entry point was unavailable.");
        }

        return new InputBackendInventory
        {
            Backend = InputBackendKind.XInput,
            Access = InventoryAccess.Available,
            Endpoints = endpoints,
            Limitation = "XInput exposes slots, not stable physical device identities.",
        };
    }

    private static InputBackendInventory CollectDirectInputView() => CollectPnpInputView(
        InputBackendKind.DirectInput,
        static (name, service) => ContainsAny(name, "joystick", "game controller", "gamepad")
            || string.Equals(service, "HidUsb", StringComparison.OrdinalIgnoreCase),
        "This passive compatibility view does not instantiate DirectInput or acquire a device.");

    private static InputBackendInventory CollectRawHidView() => CollectPnpInputView(
        InputBackendKind.RawHid,
        static (_, service) => string.Equals(service, "HidUsb", StringComparison.OrdinalIgnoreCase),
        "PnP HID presence does not prove a report descriptor or exclusive-open policy.");

    private static InputBackendInventory CollectPnpInputView(
        InputBackendKind backend,
        Func<string?, string?, bool> predicate,
        string limitation)
    {
        List<InputEndpointInventory> endpoints = [];
        try
        {
            using ManagementObjectSearcher searcher = new(
                "root\\CIMV2",
                "SELECT DeviceID, Name, Service, Status FROM Win32_PnPEntity WHERE PNPClass = 'HIDClass'");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? instanceId = Text(item, "DeviceID");
                    string? name = Text(item, "Name");
                    string? service = Text(item, "Service");
                    if (instanceId is null || !predicate(name, service))
                    {
                        continue;
                    }

                    endpoints.Add(new InputEndpointInventory
                    {
                        EndpointId = instanceId,
                        InstanceId = instanceId,
                        Name = name,
                        DeviceType = service,
                        Connected = string.Equals(Text(item, "Status"), "OK", StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
        }
        catch (ManagementException exception)
        {
            return new InputBackendInventory
            {
                Backend = backend,
                Access = exception.ErrorCode == ManagementStatus.AccessDenied
                    ? InventoryAccess.AccessDenied
                    : InventoryAccess.Unsupported,
                Limitation = limitation,
            };
        }

        return new InputBackendInventory
        {
            Backend = backend,
            Access = InventoryAccess.Available,
            Endpoints = [.. endpoints.OrderBy(endpoint => endpoint.EndpointId, StringComparer.Ordinal)],
            Limitation = limitation,
        };
    }

    private static InputBackendInventory CollectSdlView()
    {
        string appLocal = Path.Combine(AppContext.BaseDirectory, "SDL3.dll");
        string? path = File.Exists(appLocal) ? appLocal : null;
        return new InputBackendInventory
        {
            Backend = InputBackendKind.Sdl,
            Access = path is null ? InventoryAccess.Unsupported : InventoryAccess.Available,
            Limitation = path is null
                ? "SDL3.dll is not installed beside Device Lab; no runtime was loaded."
                : "SDL is present but Device Lab does not initialize its gamepad subsystem during inventory.",
        };
    }

    private static unsafe InputBackendInventory CollectRawInput()
    {
        uint count = 0;
        uint structureSize = (uint)Marshal.SizeOf<RawInputDeviceList>();
        if (GetRawInputDeviceList(null, ref count, structureSize) == uint.MaxValue)
        {
            return UnavailableBackend(InputBackendKind.RawInput, "Raw Input device count was unavailable.");
        }

        RawInputDeviceList[] devices = new RawInputDeviceList[count];
        if (count != 0 && GetRawInputDeviceList(devices, ref count, structureSize) == uint.MaxValue)
        {
            return UnavailableBackend(InputBackendKind.RawInput, "Raw Input device enumeration failed.");
        }

        List<InputEndpointInventory> endpoints = [];
        for (int index = 0; index < count; index++)
        {
            uint characters = 0;
            _ = GetRawInputDeviceInfo(devices[index].Device, RidiDeviceName, null, ref characters);
            char[] name = new char[Math.Max(characters, 1)];
            uint nameResult;
            fixed (char* namePointer = name)
            {
                nameResult = GetRawInputDeviceInfo(
                    devices[index].Device,
                    RidiDeviceName,
                    namePointer,
                    ref characters);
            }

            int terminator = Array.IndexOf(name, '\0');
            int nameLength = terminator >= 0 ? terminator : Math.Min((int)characters, name.Length);
            string? deviceName = nameResult == uint.MaxValue ? null : new string(name, 0, nameLength);
            endpoints.Add(new InputEndpointInventory
            {
                EndpointId = $"rawinput:{index}",
                InstanceId = deviceName,
                Name = deviceName,
                DeviceType = devices[index].Type switch
                {
                    RimTypeMouse => "mouse",
                    RimTypeKeyboard => "keyboard",
                    RimTypeHid => "hid",
                    _ => $"type-{devices[index].Type}",
                },
                Connected = true,
            });
        }

        return new InputBackendInventory
        {
            Backend = InputBackendKind.RawInput,
            Access = InventoryAccess.Available,
            Endpoints = endpoints,
            Limitation = "Raw Input names are session observations and do not prove exclusive ownership.",
        };
    }

    private static InputBackendInventory UnavailableBackend(InputBackendKind backend, string limitation) => new()
    {
        Backend = backend,
        Access = InventoryAccess.Unsupported,
        Limitation = limitation,
    };

    private static IReadOnlyList<ProcessInventory> CollectRelevantProcesses()
    {
        Dictionary<int, string?> commandLines = QueryProcessCommandLines();
        List<ProcessInventory> observations = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (!IsRelevant(name))
                {
                    continue;
                }

                string? path = null;
                List<string> modules = [];
                try
                {
                    path = process.MainModule?.FileName;
                    foreach (ProcessModule module in process.Modules)
                    {
                        if (IsRelevant(module.ModuleName) || IsRelevant(name))
                        {
                            modules.Add(module.FileName);
                        }
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Access is represented by missing optional fields; process presence remains useful.
                }

                observations.Add(new ProcessInventory
                {
                    ProcessId = process.Id,
                    Name = name,
                    Path = path,
                    CommandLine = commandLines.GetValueOrDefault(process.Id),
                    LoadedModulePaths = [.. modules.Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(module => module, StringComparer.OrdinalIgnoreCase)],
                });
            }
        }

        return [.. observations.OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.ProcessId)];
    }

    private static Dictionary<int, string?> QueryProcessCommandLines()
    {
        Dictionary<int, string?> lines = [];
        try
        {
            using ManagementObjectSearcher searcher = new(
                "root\\CIMV2",
                "SELECT ProcessId, CommandLine FROM Win32_Process");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    if (int.TryParse(Text(item, "ProcessId"), CultureInfo.InvariantCulture, out int processId))
                    {
                        lines[processId] = Text(item, "CommandLine");
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // Process enumeration does not depend on command-line access.
        }

        return lines;
    }

    private static IReadOnlyList<ServiceInventory> CollectRelevantServices()
    {
        List<ServiceInventory> services = [];
        try
        {
            using ManagementObjectSearcher searcher = new(
                "root\\CIMV2",
                "SELECT Name, DisplayName, State, PathName, ProcessId FROM Win32_Service");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? name = Text(item, "Name");
                    string? displayName = Text(item, "DisplayName");
                    string? path = Text(item, "PathName");
                    if (name is null || !(IsRelevant(name) || IsRelevant(displayName) || IsRelevant(path)))
                    {
                        continue;
                    }

                    services.Add(new ServiceInventory
                    {
                        Name = name,
                        DisplayName = displayName,
                        State = Text(item, "State"),
                        PathName = path,
                        ProcessId = int.TryParse(Text(item, "ProcessId"), CultureInfo.InvariantCulture, out int id)
                            && id != 0
                                ? id
                                : null,
                    });
                }
            }
        }
        catch (ManagementException)
        {
            // A denied service lane does not invalidate endpoint inventory.
        }

        return [.. services.OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<ScheduledTaskInventory> CollectRelevantScheduledTasks()
    {
        List<ScheduledTaskInventory> tasks = [];
        try
        {
            using ManagementObjectSearcher searcher = new(
                "root\\Microsoft\\Windows\\TaskScheduler",
                "SELECT TaskName, TaskPath, State, Enabled FROM MSFT_ScheduledTask");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string? name = Text(item, "TaskName");
                    string? path = Text(item, "TaskPath");
                    if (!(IsRelevant(name) || IsRelevant(path)))
                    {
                        continue;
                    }

                    tasks.Add(new ScheduledTaskInventory
                    {
                        Path = $"{path}{name}",
                        State = Text(item, "State"),
                        Enabled = bool.TryParse(Text(item, "Enabled"), out bool enabled) ? enabled : null,
                    });
                }
            }
        }
        catch (ManagementException)
        {
            // Scheduled-task inventory is optional on Windows editions lacking this provider.
        }

        return [.. tasks.OrderBy(task => task.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<NativeBinaryInventory> CollectNativeBinaries(
        IReadOnlyList<ProcessInventory> processes,
        IReadOnlyList<ServiceInventory> services)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        string systemDirectory = Environment.SystemDirectory;
        foreach (string name in new[] { "xinput1_4.dll", "hid.dll", "setupapi.dll", "cfgmgr32.dll" })
        {
            paths.Add(Path.Combine(systemDirectory, name));
        }

        foreach (ProcessInventory process in processes)
        {
            if (process.Path is not null)
            {
                paths.Add(process.Path);
            }

            foreach (string module in process.LoadedModulePaths)
            {
                if (IsRelevant(Path.GetFileName(module)))
                {
                    paths.Add(module);
                }
            }
        }

        foreach (ServiceInventory service in services)
        {
            string? executable = ExtractExecutablePath(service.PathName);
            if (executable is not null)
            {
                paths.Add(executable);
            }
        }

        List<NativeBinaryInventory> binaries = [];
        foreach (string path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path) && NativePeInspector.TryInspect(path, out NativeBinaryInventory? binary))
            {
                binaries.Add(binary);
            }
        }

        return binaries;
    }

    private static IReadOnlyList<ResourceConflictInventory> DerivePresenceConflicts(
        IReadOnlyList<ProcessInventory> processes,
        IReadOnlyList<ServiceInventory> services)
    {
        List<ResourceConflictInventory> conflicts = [];
        foreach (string owner in processes.Select(process => process.Name)
            .Concat(services.Select(service => service.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? resource = owner.Contains("hidhide", StringComparison.OrdinalIgnoreCase)
                || owner.Contains("hidmaestro", StringComparison.OrdinalIgnoreCase)
                ? "controller-routing"
                : owner.Contains("msi", StringComparison.OrdinalIgnoreCase)
                    || owner.Contains("center", StringComparison.OrdinalIgnoreCase)
                    || owner.Contains("handheld", StringComparison.OrdinalIgnoreCase)
                    ? "vendor-control"
                    : null;
            if (resource is not null)
            {
                conflicts.Add(new ResourceConflictInventory
                {
                    ResourceId = resource,
                    Owner = owner,
                    Evidence = ConflictEvidenceKind.PresenceOnly,
                });
            }
        }

        return [.. conflicts.OrderBy(conflict => conflict.ResourceId, StringComparer.Ordinal)
            .ThenBy(conflict => conflict.Owner, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsRelevant(string? value) => value is not null
        && RelevantNameFragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string? value, params string[] fragments) => value is not null
        && fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static uint? UInt32(ManagementBaseObject source, string property) =>
        uint.TryParse(Text(source, property), CultureInfo.InvariantCulture, out uint value) ? value : null;

    private static byte? Byte(ManagementBaseObject source, string property) =>
        byte.TryParse(Text(source, property), CultureInfo.InvariantCulture, out byte value) ? value : null;

    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string trimmed = Environment.ExpandEnvironmentVariables(command.Trim());
        if (trimmed[0] == '"')
        {
            int close = trimmed.IndexOf('"', 1);
            return close > 1 ? trimmed[1..close] : null;
        }

        int exe = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? trimmed[..(exe + 4)] : null;
    }

    [GeneratedRegex(@"VEN_(?<ven>[0-9A-Fa-f]{4})&DEV_(?<dev>[0-9A-Fa-f]{4})")]
    private static partial Regex PciIdentifiers();

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
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
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilities
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public XInputGamepad Gamepad;
        public XInputVibration Vibration;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public nint Device;
        public uint Type;
    }

    [LibraryImport("xinput1_4.dll")]
    private static partial uint XInputGetCapabilities(
        uint userIndex,
        uint flags,
        out XInputCapabilities capabilities);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceList")]
    private static partial uint GetRawInputDeviceList(
        [Out] RawInputDeviceList[]? rawInputDeviceList,
        ref uint numberOfDevices,
        uint size);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static unsafe partial uint GetRawInputDeviceInfo(
        nint device,
        uint command,
        char* data,
        ref uint size);
}
