using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Projects Device capabilities into the common widget vocabulary without owning hardware.</summary>
internal sealed class DeviceWidgetSource(DeviceCoordinator coordinator, IDeviceOverlaySource overlay) : ICommonPluginOverlaySource
{
    private readonly Dictionary<string, DeviceCapabilityView> _views = new(StringComparer.Ordinal);
    private string _scope = "";
    private long _generation;
    private long _sequence;
    private PluginInstanceIdentity? _identity;

    public PluginOverlayInstance[] Snapshot()
    {
        var snapshot = overlay.Snapshot();
        string? plugin = coordinator.InstalledPackage?.Manifest?.Id;
        var views = coordinator.Capabilities.Snapshot();
        if (!snapshot.Visible || plugin is null || views.Count == 0) { _views.Clear(); _scope = ""; return []; }
        var first = views[0].Projection.State;
        string scope = $"{plugin}:{first.CycleGeneration}:{first.DescriptorGeneration}:{coordinator.ManualTdpMode.Unified}";
        if (_scope != scope) { _scope = scope; _generation++; }
        _identity = new(plugin, "device");
        List<PluginAction> actions = [];
        List<PluginUiContribution> controls = [];
        List<PluginWidget> widgets = [];
        _views.Clear();
        foreach (var view in views.Where(item => item.Descriptor.SupportsRead).Take(32))
        {
            var descriptor = view.Descriptor;
            var row = snapshot.Capabilities.FirstOrDefault(item => item.CapabilityId == descriptor.CapabilityId
                && item.InstanceId == descriptor.InstanceId);
            if (row is null) { continue; }
            string key = KeyFor(descriptor.CapabilityId, descriptor.InstanceId);
            _views[key] = view;
            PluginValue value = Value(view.Projection.State.ObservedValue);
            PluginUiKind kind = PluginUiKind.Status;
            PluginSetting? argument = null;
            if (row.Writable && descriptor.ValueKind == CapabilityValueKind.Integer && value.Number is not null
                && descriptor.Minimum is { } min && descriptor.Maximum is { } max && min < max)
            { kind = PluginUiKind.Slider; argument = new("value", row.Title, PluginSettingKind.Number, value, min, max); }
            else if (row.Writable && descriptor.ValueKind == CapabilityValueKind.Boolean && value.Boolean is not null)
            { kind = PluginUiKind.Toggle; argument = new("value", row.Title, PluginSettingKind.Boolean, value); }
            if (argument is not null) { actions.Add(new(key, row.Title, [argument])); }
            controls.Add(new(key, row.Title, "device", kind, key, argument is null ? null : key,
                argument is null ? null : "value"));
            widgets.Add(new(key, row.Title, [key], EnabledStateKey: "enabled." + key, NavigationCategory: "device"));
        }
        return [new(_identity, plugin, _generation, new(actions, controls, widgets), snapshot.Status, true, null)];
    }

    public PluginStatePublication[] State(PluginInstanceIdentity identity)
    {
        if (_identity != identity) { return []; }
        var current = coordinator.Capabilities.Snapshot();
        var presentation = overlay.Snapshot();
        List<PluginStatePublication> states = [];
        foreach (var (key, original) in _views)
        {
            var view = current.FirstOrDefault(item => item.Descriptor.CapabilityId == original.Descriptor.CapabilityId
                && item.Descriptor.InstanceId == original.Descriptor.InstanceId);
            if (view is null || view.Projection.State.CycleGeneration != original.Projection.State.CycleGeneration
                || view.Projection.State.DescriptorGeneration != original.Projection.State.DescriptorGeneration) { continue; }
            states.Add(new(identity, _generation, ++_sequence, key, Value(view.Projection.State.ObservedValue), PluginStateOrigin.HardwareReadback));
            bool enabled = presentation.Capabilities.Any(item => item.CapabilityId == view.Descriptor.CapabilityId
                && item.InstanceId == view.Descriptor.InstanceId && item.CanInvoke);
            states.Add(new(identity, _generation, ++_sequence, "enabled." + key, new(Boolean: enabled), PluginStateOrigin.HardwareReadback));
        }
        return states.ToArray();
    }

    public async Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
    {
        Guid operation = Guid.NewGuid();
        Snapshot();
        if (_identity != identity || generation != _generation || !_views.TryGetValue(action, out var view)
            || arguments.Count != 1 || !arguments.TryGetValue("value", out var value))
        { return new(operation, PluginActionOutcome.Rejected, "The Device widget changed. Select it again."); }
        CapabilityValue? requested = view.Descriptor.ValueKind switch
        {
            CapabilityValueKind.Integer when value.Number is { } number && double.IsFinite(number)
                && number == Math.Truncate(number) && number is >= int.MinValue and <= int.MaxValue =>
                new() { Kind = CapabilityValueKind.Integer, IntegerValue = (int)number },
            CapabilityValueKind.Boolean when value.Boolean is { } enabled =>
                new() { Kind = CapabilityValueKind.Boolean, BooleanValue = enabled },
            _ => null,
        };
        if (requested is null) { return new(operation, PluginActionOutcome.Rejected, "Invalid Device widget value."); }
        var state = view.Projection.State;
        var result = await coordinator.ExecuteCapabilityAsync(view.Descriptor.CapabilityId, view.Descriptor.InstanceId,
            requested, TimeSpan.FromSeconds(5), cancellationToken: cancellationToken,
            expectedCycle: state.CycleGeneration, expectedDescriptors: state.DescriptorGeneration).ConfigureAwait(false);
        return new(operation, result.Outcome == CommandOutcome.AppliedVerified ? PluginActionOutcome.AppliedVerified
            : result.Outcome == CommandOutcome.Rejected ? PluginActionOutcome.Rejected : PluginActionOutcome.Unconfirmed, result.Reason?.Detail);
    }

    internal static string KeyFor(string capabilityId, string? instanceId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{capabilityId.Length}:{capabilityId}{instanceId}"))).ToLowerInvariant();

    internal static PluginValue Value(CapabilityValue? value) => value?.Kind switch
    {
        CapabilityValueKind.Integer => new(Number: value.IntegerValue),
        CapabilityValueKind.Boolean => new(Boolean: value.BooleanValue),
        CapabilityValueKind.Choice => new(Text: value.ChoiceValue ?? "Unavailable"),
        _ => new(Text: value?.TextValue ?? "Unavailable"),
    };
}
