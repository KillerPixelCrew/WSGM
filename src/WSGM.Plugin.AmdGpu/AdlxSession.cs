// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.AmdGpu;

internal sealed unsafe partial class AdlxSession : IDriverSession
{
    private readonly AdlxObject? _displays;
    private readonly AdlxObject? _displays3;
    private readonly long _epoch;
    private readonly AdlxObject? _graphics;
    private readonly AdlxObject? _graphics1;
    private readonly AdlxObject? _graphics2;
    private readonly AdlxObject? _graphics3;
    private readonly Action<string, string> _report;
    private readonly List<AdlxObject> _services = [];
    private AdlDitherApi? _dither;
    private bool _initialized;
    private nint _library;
    private nint _mapping;
    private List<AdlxObject> _objects = [];
    private nint _system;

    internal AdlxSession(Action<string, string> report)
    {
        _report = report;
        _library = NativeLibrary.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "amdadlx64.dll"));
        try
        {
            ulong runtimeVersion = 0;
            AdlxNative.Check(
                ((delegate* unmanaged[Cdecl]<ulong*, int>)NativeLibrary.GetExport(_library, "ADLXQueryFullVersion"))
                (&runtimeVersion), "QueryFullVersion");
            var version = Math.Min(AdlxNative.HeaderVersion, runtimeVersion);
            nint system = 0;
            nint mapping = 0;
            int status;
            if (NativeLibrary.TryGetExport(_library, "ADLXInitialize2", out var initialize))
            {
                status = ((delegate* unmanaged[Cdecl]<ulong, nint*, nint*, int>)initialize)(version, &system, &mapping);
            }
            else
            {
                status = ((delegate* unmanaged[Cdecl]<ulong, nint*, int>)NativeLibrary.GetExport(_library,
                        "ADLXInitialize"))
                    (version, &system);
            }

            // An already-initialized ADLX owner must not be terminated by this package.
            if (status != 0)
            {
                throw new DriverFailure($"ADLX initialization returned {status}.");
            }

            _initialized = true;
            _epoch = AdlxNative.Epoch;
            _system = system != 0 ? system : throw new DriverFailure("ADLX supplied no system service.");
            _mapping = mapping;
            _graphics = Service(() => AdlxNative.Interface(_system, 7), "3d");
            _displays = Service(() => AdlxNative.Interface(_system, 3), "displays");
            if (_graphics is not null)
            {
                _graphics1 = Service(() => AdlxNative.Query(_graphics.Pointer, "IADLX3DSettingsServices1"), "3d1");
                _graphics2 = Service(() => AdlxNative.Query(_graphics.Pointer, "IADLX3DSettingsServices2"), "3d2");
                _graphics3 = Service(() => AdlxNative.Query(_graphics.Pointer, "IADLX3DSettingsServices3"), "3d3");
            }

            if (_displays is not null)
            {
                _displays3 = Service(() => AdlxNative.Query(_displays.Pointer, "IADLXDisplayServices3"), "displays3");
            }

            if (_mapping != 0)
            {
                Try("dithering", () => _dither = new AdlDitherApi());
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>ADLX reads each control live; there is nothing to load per pass.</summary>
    public void BeginPass()
    {
    }

    public DriverModel Discover()
    {
        var objects = new List<AdlxObject>();
        var sections = new List<CapabilitySection>();
        var controls = new List<DriverControl>();
        try
        {
            using var gpuList = AdlxNative.Interface(_system, 1);
            var gpus = AdlxNative.Items(gpuList.Pointer);
            objects.AddRange(gpus);
            if (gpus.Count == 0)
            {
                throw new DriverFailure("No AMD GPU is available.", lost: true);
            }

            foreach (var gpu in gpus)
            {
                Try("gpu", () =>
                {
                    var pnp = AdlxNative.Text(gpu.Pointer, 9);
                    if (!pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    var instance = "pci-" + Hash(pnp);
                    var section = "amd." + instance;
                    var target = new AmdTarget(pnp, null, null);
                    var before = controls.Count;
                    AddGraphics(gpu.Pointer, target, instance, section, objects, controls);
                    if (controls.Count > before)
                    {
                        sections.Add(DriverDescriptors.Section(section, AdlxNative.Text(gpu.Pointer, 7)));
                    }
                });
            }

            if (_displays is not null)
            {
                using var list = AdlxNative.Interface(_displays.Pointer, 4);
                var displays = AdlxNative.Items(list.Pointer);
                objects.AddRange(displays);
                foreach (var display in displays)
                {
                    Try("display", () =>
                    {
                        using var gpu = AdlxNative.Interface(display.Pointer, 12);
                        var pnp = AdlxNative.Text(gpu.Pointer, 9);
                        if (!pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        var id = AdlxNative.Size(display.Pointer, 13);
                        var edid = AdlxNative.Text(display.Pointer, 7);
                        var target = new AmdTarget(pnp, id, edid);
                        var instance = (AdlxNative.Integer(display.Pointer, 5) == 15 ? "internal-" : "output-")
                                       + Hash(pnp + "/" + id + "/" + edid);
                        var section = "amd." + instance;
                        var before = controls.Count;
                        AddDisplay(display.Pointer, target, instance, section, objects, controls);
                        if (controls.Count > before)
                        {
                            sections.Add(DriverDescriptors.Section(section,
                                "AMD " + AdlxNative.Text(display.Pointer, 6), true));
                        }
                    });
                }
            }

            var previous = _objects;
            _objects = objects;
            Release(previous);
            return new DriverModel(sections, controls);
        }
        catch
        {
            Release(objects);
            throw;
        }
    }

    public ApplicationProfileSyncResult Sync(ApplicationProfileSync sync, WriteAdmission admission,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // ADLX controls are GPU-wide. WSGM applies/restores Switched values using its existing game identity.
        return new ApplicationProfileSyncResult(0, 0, []);
    }

    public void Dispose()
    {
        if (_library == 0)
        {
            return;
        }

        try
        {
            Release(_objects);
            _dither?.Dispose();
            _dither = null;
            Release(_services);
            if (_initialized && _epoch == AdlxNative.Epoch)
            {
                ((delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(_library, "ADLXTerminate"))();
                _initialized = false;
            }
        }
        finally
        {
            NativeLibrary.Free(_library);
            _library = 0;
            _system = _mapping = 0;
        }
    }

    internal void Validate(AmdTarget target)
    {
        using var list = target.DisplayId is null
            ? AdlxNative.Interface(_system, 1)
            : AdlxNative.Interface(_displays!.Pointer, 4);
        var items = AdlxNative.Items(list.Pointer);
        try
        {
            foreach (var item in items)
            {
                if (target.DisplayId is null)
                {
                    if (AdlxNative.Text(item.Pointer, 9) == target.GpuPnp)
                    {
                        return;
                    }
                }
                else if (AdlxNative.Size(item.Pointer, 13) == target.DisplayId &&
                         AdlxNative.Text(item.Pointer, 7) == target.Edid)
                {
                    using var gpu = AdlxNative.Interface(item.Pointer, 12);
                    if (AdlxNative.Text(gpu.Pointer, 9) == target.GpuPnp)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            Release(items);
        }

        throw new DriverFailure("The AMD adapter or display changed before the write; refresh its controls.");
    }

    private AdlxObject? Service(Func<AdlxObject?> get, string key)
    {
        AdlxObject? result = null;
        Try(key, () =>
        {
            result = get();
            if (result is not null)
            {
                _services.Add(result);
            }
        });
        return result;
    }

    private void Try(string key, Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is not DriverFailure { Lost: true })
        {
            _report(key, error.Message);
        }
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
    }

    private static void Release(List<AdlxObject> objects)
    {
        for (var index = objects.Count - 1; index >= 0; index--)
        {
            objects[index].Dispose();
        }

        objects.Clear();
    }
}

internal sealed record AmdTarget(string GpuPnp, nuint? DisplayId, string? Edid);

internal sealed class AdlxControl(
    CapabilityDescriptor descriptor,
    Func<CapabilityValue> read,
    Action<CapabilityValue, WriteAdmission> write)
    : DriverControl(descriptor)
{
    internal override CapabilityValue Read()
    {
        return read();
    }

    internal override void Write(CapabilityValue value, WriteAdmission admission)
    {
        write(value, admission);
    }
}

internal static class AmdValue
{
    internal static string Encode(int value)
    {
        return "v" + value.ToString(CultureInfo.InvariantCulture);
    }

    internal static int Decode(CapabilityValue value)
    {
        return int.Parse(value.ChoiceValue!.AsSpan(1), CultureInfo.InvariantCulture);
    }
}
