// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.NvidiaGpu;

internal sealed class NvSession : IDriverSession
{
    private readonly NvApi _api;
    private readonly NvProfiles _profiles;
    private readonly Action<string, string> _report;
    private Dictionary<string, NvSettingControl> _settings = [];

    internal NvSession(string stateDirectory, Action<string, string> report)
    {
        _report = report;
        _api = new NvApi();
        try
        {
            _profiles = new NvProfiles(_api, stateDirectory);
        }
        catch
        {
            _api.Dispose();
            throw;
        }
    }

    public DriverModel Discover()
    {
        var outputs = _api.Displays(); // NVIDIA present does not imply that it drives the laptop panel.
        _api.Load();
        var ids = _api.SettingIds();
        var sections = new List<CapabilitySection>();
        var controls = new List<DriverControl>();
        var settings = new Dictionary<string, NvSettingControl>();
        foreach (var group in NvSettings.All.GroupBy(setting => setting.Group))
        {
            var section = "nvidia." + group.Key;
            var before = controls.Count;
            foreach (var setting in group.Where(setting => ids.Contains(setting.Id)))
            {
                Try("driver." + setting.Id.ToString("x8"), () =>
                {
                    var values = _api.Values(setting.Id);
                    if (values.Length == 0 && setting.Documented)
                    {
                        values = setting.Values.Select(value => value.Value).Distinct().ToArray();
                    }

                    if (values.Length == 0)
                    {
                        return;
                    }

                    try
                    {
                        var current = _api.Get(_api.GlobalProfile(), setting.Id);
                        values = values.Append(current.Value).Distinct().ToArray();
                    }
                    catch (DriverFailure failure) when (!failure.Lost)
                    {
                        _report("driver." + setting.Id.ToString("x8"), failure.Message);
                    }

                    if (setting.Id == 0x1094f157 && values.Contains(0u) && values.Contains(1u))
                    {
                        controls.Add(new NvGsyncControl(_api, section));
                        return;
                    }

                    var control = new NvSettingControl(_api, setting, section, values);
                    // Checks the DWORD type. Display-output controls are discovered separately.
                    controls.Add(control);
                    settings.Add(control.Descriptor.CapabilityId, control);
                });
            }

            if (controls.Count > before)
            {
                var title = group.Key switch
                {
                    "performance" => "Performance",
                    "quality" => "Quality",
                    "display" => "Display",
                    "dlss" => "DLSS",
                    "vr" => "VR",
                    _ => group.Key
                };
                sections.Add(DriverDescriptors.Section(section, "NVIDIA " + title, group.Key == "display"));
            }
        }

        foreach (var output in outputs)
        {
            var instance = output.Target is { DevicePath.Length: > 0 } target
                ? "output-" +
                  Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.DevicePath.ToUpperInvariant())))
                      [..24].ToLowerInvariant()
                : "output-" + output.Id.ToString("x8");
            var section = "nvidia." + instance;
            var before = controls.Count;
            AddOutput(controls, output, instance, section);
            if (controls.Count > before)
            {
                sections.Add(DriverDescriptors.Section(section,
                    "NVIDIA " + (output.Target?.FriendlyName ?? "display " + output.Id.ToString("X8")), true));
            }
        }

        _settings = settings;
        return new DriverModel(sections, controls);
    }

    public ApplicationProfileSyncResult Sync(ApplicationProfileSync sync, CancellationToken token)
    {
        return _profiles.Sync(sync, _settings, token);
    }

    public void Dispose()
    {
        _api.Dispose();
    }

    private void AddOutput(List<DriverControl> controls, NvOutput output, string instance, string section)
    {
        Try(instance + "/vrr", () =>
        {
            _api.Vrr(output.Id);
            controls.Add(new NvReadOnlyControl(
                DriverDescriptors.ReadOnly("display.gsync-status", instance, "G-SYNC state", section,
                    [("unsupported", "Unavailable"), ("off", "Off"), ("enabled", "Enabled"), ("active", "Active")]),
                () => (_api.Vrr(output.Id) & 0x13) switch
                {
                    var flags when (flags & 2) == 0 => "unsupported",
                    var flags when (flags & 16) != 0 => "active",
                    var flags when (flags & 1) != 0 => "enabled",
                    _ => "off"
                }));
        });
        Try(instance + "/color", () =>
        {
            var current = _api.Color(output.Id, 1);
            foreach (var field in NvColorField.All)
            {
                var choices = field.Values.Where(value => _api.SupportsColor(output.Id,
                    field.WithValue(current, value.Value))).ToArray();
                if (choices.Length > 0)
                {
                    controls.Add(new NvColorControl(_api, output, instance, section, field, choices));
                }
            }
        });
        Try(instance + "/dither", () =>
        {
            if (!_api.HasFunction(0x932ac8fb) || !_api.HasFunction(0xdf0dfcdd))
            {
                return;
            }

            var current = _api.Dither(output.Id);
            if (current.State > 2)
            {
                return;
            }

            controls.Add(new NvDitherControl(_api, output, instance, section, 0,
                [(0, "Auto"), (1, "Enabled"), (2, "Disabled")]));
            var bits = new (uint, string)[] { (0, "6-bit"), (1, "8-bit"), (2, "10-bit") }
                .Where(value => (current.BitsCaps & (1u << (int)value.Item1)) != 0).ToArray();
            var modes = new (uint, string)[]
            {
                (0, "Spatial dynamic"), (1, "Spatial static"), (2, "Spatial dynamic 2x2"),
                (3, "Spatial static 2x2"), (4, "Temporal")
            }.Where(value => (current.ModeCaps & (1u << (int)value.Item1)) != 0).ToArray();
            if (bits.Length > 0 && current.State == 1)
            {
                controls.Add(new NvDitherControl(_api, output, instance, section, 1, bits));
            }

            if (modes.Length > 0 && current.State == 1)
            {
                controls.Add(new NvDitherControl(_api, output, instance, section, 2, modes));
            }
        });
        Try(instance + "/hdr-output", () =>
        {
            if (!_api.HasFunction(0x81fed88d) || !_api.HasFunction(0x98e7661a))
            {
                return;
            }

            var flags = _api.HdrCapabilities(output.Id);
            if ((flags & 1) == 0)
            {
                return;
            }

            _api.OutputMode(output.Id);
            var values = new List<(uint, string)> { (0, "SDR"), (1, "HDR10") };
            if ((flags & 0x80) != 0) // isHdr10PlusGamingSupported, not merely HDR10+ video support
            {
                values.Add((2, "HDR10+ Gaming"));
            }

            controls.Add(new NvOutputModeControl(_api, output, instance, section, values));
        });
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
}

internal sealed class NvSettingControl : DriverControl
{
    private readonly INvProfiles _api;

    internal NvSettingControl(INvProfiles api, NvSettingDefinition setting, string section, IEnumerable<uint> values)
        : base(DriverDescriptors.Choice("driver." + setting.Id.ToString("x8"), "driver", setting.Label, section,
            setting.Native ? CapabilityProfileScope.NativePerApplication : CapabilityProfileScope.GlobalOnly,
            values.Select(value => (Encode(value), setting.Values.FirstOrDefault(option => option.Value == value).Label
                                                   ?? "Driver value 0x" + value.ToString("X8")))))
    {
        _api = api;
        Setting = setting;
    }

    internal NvSettingDefinition Setting { get; }

    internal override void ProbeSupport(CapabilityValue current)
    {
        // Save identical explicit values. Reload discards staged inherited/default values without
        // materializing a persistent user override.
        _api.Load();
        try
        {
            var profile = _api.GlobalProfile();
            var native = _api.Get(profile, Setting.Id);
            _api.Set(profile, Setting.Id, native.Value);
            if (native.Explicit)
            {
                _api.Save();
            }
        }
        finally
        {
            _api.Load();
        }
    }

    internal override CapabilityValue Read()
    {
        _api.Load();
        return CapabilityValue.Choice(Encode(_api.Get(_api.GlobalProfile(), Setting.Id).Value));
    }

    internal override void Write(CapabilityValue value)
    {
        _api.Load();
        var profile = _api.GlobalProfile();
        try
        {
            _api.Get(profile, Setting.Id);
        }
        catch (DriverFailure failure) when (!failure.Lost)
        {
            // Support/type came from the driver table; a failed optional read cannot gate the write.
        }

        _api.Set(profile, Setting.Id, Decode(value));
        _api.Save();
    }

    internal static string Encode(uint value)
    {
        return "v" + value.ToString("x8");
    }

    internal static uint Decode(CapabilityValue value)
    {
        return uint.Parse(value.ChoiceValue!.AsSpan(1), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);
    }
}

internal sealed class NvReadOnlyControl(CapabilityDescriptor descriptor, Func<string> read) : DriverControl(descriptor)
{
    internal override CapabilityValue Read()
    {
        return CapabilityValue.Choice(read());
    }

    internal override void Write(CapabilityValue value)
    {
        throw new DriverFailure("This NVIDIA state is read only.");
    }
}
