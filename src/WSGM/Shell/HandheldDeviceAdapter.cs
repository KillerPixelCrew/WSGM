using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibHandheld;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Settings;
using P = LibHandheld.Contracts;

namespace WSGM.Shell;

internal sealed partial class HandheldDeviceAdapter : IDevicePlugin, P.IHandheldObserver
{
    private static CapabilityDescriptor ToSdk(P.CapabilityDescriptor value)
    {
        var result = new CapabilityDescriptor
        {
            CapabilityId = value.CapabilityId,
            InstanceId = value.InstanceId,
            Role = (CapabilityRole)value.Role,
            ValueKind = (CapabilityValueKind)value.ValueKind,
            SupportsRead = value.SupportsRead,
            SupportsWrite = value.SupportsWrite,
            SupportsAction = value.SupportsAction,
            Minimum = value.Minimum,
            Maximum = value.Maximum,
            Step = value.Step,
            Unit = (CapabilityUnit)value.Unit,
            PairedPowerLimitId = value.PairedPowerLimitId,
            Choices = Array.AsReadOnly(value.Choices.Select(ToSdk).ToArray()),
            PowerPresets = Array.AsReadOnly(value.PowerPresets.Select(ToSdk).ToArray()),
            MaximumLength = value.MaximumLength,
            AvailableOnAc = value.AvailableOnAc,
            AvailableOnDc = value.AvailableOnDc,
            Persistence = (CapabilityPersistence)value.Persistence,
            ProfileScope = (CapabilityProfileScope)value.ProfileScope,
            ApplyTiming = (CapabilityApplyTiming)value.ApplyTiming,
            Display = Display(value.Label)
        };
        return Place(result);
    }

    private static CapabilityDescriptorSet ToSdk(P.CapabilityDescriptorSet value)
    {
        var result = new CapabilityDescriptorSet
        {
            Generation = value.Generation,
            CycleGeneration = value.CycleGeneration,
            Descriptors = Array.AsReadOnly(value.Descriptors.Select(ToSdk).ToArray()),
            Sections = Sections
        };
        return result;
    }

    private static CapabilityState ToSdk(P.CapabilityState value)
    {
        var result = new CapabilityState
        {
            CapabilityId = value.CapabilityId,
            InstanceId = value.InstanceId,
            Available = value.Available,
            Reason = value.Reason is { } itemReason ? ToSdk(itemReason) : null,
            ObservedValue = value.ObservedValue is { } itemObservedValue ? ToSdk(itemObservedValue) : null,
            Quality = (HardwareStateQuality)value.Quality,
            ObservedAt = value.ObservedAt,
            DescriptorGeneration = value.DescriptorGeneration,
            CycleGeneration = value.CycleGeneration
        };
        return result;
    }

    private static P.CapabilityState ToLibrary(CapabilityState value)
    {
        var result = new P.CapabilityState
        {
            CapabilityId = value.CapabilityId,
            InstanceId = value.InstanceId,
            Available = value.Available,
            Reason = value.Reason is { } itemReason ? ToLibrary(itemReason) : null,
            ObservedValue = value.ObservedValue is { } itemObservedValue ? ToLibrary(itemObservedValue) : null,
            Quality = (P.HardwareStateQuality)value.Quality,
            ObservedAt = value.ObservedAt,
            DescriptorGeneration = value.DescriptorGeneration,
            CycleGeneration = value.CycleGeneration
        };
        return result;
    }

    private static CapabilityValue ToSdk(P.CapabilityValue value)
    {
        var result = new CapabilityValue
        {
            Kind = (CapabilityValueKind)value.Kind,
            BooleanValue = value.BooleanValue,
            IntegerValue = value.IntegerValue,
            ChoiceValue = value.ChoiceValue,
            ColorValue = value.ColorValue,
            CurveValue = Array.AsReadOnly(value.CurveValue.Select(ToSdk).ToArray()),
            TextValue = value.TextValue
        };
        return result;
    }

    private static P.CapabilityValue ToLibrary(CapabilityValue value)
    {
        var result = new P.CapabilityValue
        {
            Kind = (P.CapabilityValueKind)value.Kind,
            BooleanValue = value.BooleanValue,
            IntegerValue = value.IntegerValue,
            ChoiceValue = value.ChoiceValue,
            ColorValue = value.ColorValue,
            CurveValue = Array.AsReadOnly(value.CurveValue.Select(ToLibrary).ToArray()),
            TextValue = value.TextValue
        };
        return result;
    }

    private static CapabilityCommand ToSdk(P.CapabilityCommand value)
    {
        var result = new CapabilityCommand
        {
            CommandId = value.CommandId,
            CapabilityId = value.CapabilityId,
            InstanceId = value.InstanceId,
            RequestedValue = value.RequestedValue is { } itemRequestedValue ? ToSdk(itemRequestedValue) : null,
            PairedPowerLimitWatts = value.PairedPowerLimitWatts,
            ExpectedDescriptorGeneration = value.ExpectedDescriptorGeneration,
            ExpectedCycleGeneration = value.ExpectedCycleGeneration,
            Deadline = Deadline.After(value.Deadline.Remaining)
        };
        return result;
    }

    private static P.CapabilityCommand ToLibrary(CapabilityCommand value)
    {
        var result = new P.CapabilityCommand
        {
            CommandId = value.CommandId,
            CapabilityId = value.CapabilityId,
            InstanceId = value.InstanceId,
            RequestedValue = value.RequestedValue is { } itemRequestedValue ? ToLibrary(itemRequestedValue) : null,
            PairedPowerLimitWatts = value.PairedPowerLimitWatts,
            ExpectedDescriptorGeneration = value.ExpectedDescriptorGeneration,
            ExpectedCycleGeneration = value.ExpectedCycleGeneration,
            Deadline = P.Deadline.After(value.Deadline.Remaining)
        };
        return result;
    }

    private static CapabilityCommandResult ToSdk(P.CapabilityCommandResult value)
    {
        var result = new CapabilityCommandResult
        {
            CommandId = value.CommandId,
            Outcome = (CommandOutcome)value.Outcome,
            Reason = value.Reason is { } itemReason ? ToSdk(itemReason) : null,
            ReadbackValue = value.ReadbackValue is { } itemReadbackValue ? ToSdk(itemReadbackValue) : null,
            Rollback = (RollbackResult)value.Rollback,
            CompletedAt = value.CompletedAt
        };
        return result;
    }

    private static P.CapabilityCommandResult ToLibrary(CapabilityCommandResult value)
    {
        var result = new P.CapabilityCommandResult
        {
            CommandId = value.CommandId,
            Outcome = (P.CommandOutcome)value.Outcome,
            Reason = value.Reason is { } itemReason ? ToLibrary(itemReason) : null,
            ReadbackValue = value.ReadbackValue is { } itemReadbackValue ? ToLibrary(itemReadbackValue) : null,
            Rollback = (P.RollbackResult)value.Rollback,
            CompletedAt = value.CompletedAt
        };
        return result;
    }

    private static CanonicalControllerSample ToSdk(P.CanonicalControllerSample value)
    {
        var result = new CanonicalControllerSample
        {
            Timestamp = value.Timestamp,
            Buttons = (CanonicalButtons)value.Buttons,
            LeftStickX = value.LeftStickX,
            LeftStickY = value.LeftStickY,
            RightStickX = value.RightStickX,
            RightStickY = value.RightStickY,
            LeftTrigger = value.LeftTrigger,
            RightTrigger = value.RightTrigger,
            LeftPadX = value.LeftPadX,
            LeftPadY = value.LeftPadY,
            LeftPadForce = value.LeftPadForce,
            RightPadX = value.RightPadX,
            RightPadY = value.RightPadY,
            RightPadForce = value.RightPadForce,
            LeftStickForce = value.LeftStickForce,
            RightStickForce = value.RightStickForce,
            Motion = value.Motion is { } itemMotion ? ToSdk(itemMotion) : null
        };
        return result;
    }

    private static P.CanonicalControllerSample ToLibrary(CanonicalControllerSample value)
    {
        var result = new P.CanonicalControllerSample
        {
            Timestamp = value.Timestamp,
            Buttons = (P.CanonicalButtons)value.Buttons,
            LeftStickX = value.LeftStickX,
            LeftStickY = value.LeftStickY,
            RightStickX = value.RightStickX,
            RightStickY = value.RightStickY,
            LeftTrigger = value.LeftTrigger,
            RightTrigger = value.RightTrigger,
            LeftPadX = value.LeftPadX,
            LeftPadY = value.LeftPadY,
            LeftPadForce = value.LeftPadForce,
            RightPadX = value.RightPadX,
            RightPadY = value.RightPadY,
            RightPadForce = value.RightPadForce,
            LeftStickForce = value.LeftStickForce,
            RightStickForce = value.RightStickForce,
            Motion = value.Motion is { } itemMotion ? ToLibrary(itemMotion) : null
        };
        return result;
    }

    private static MotionSample ToSdk(P.MotionSample value)
    {
        var result = new MotionSample
        {
            GyroX = value.GyroX,
            GyroY = value.GyroY,
            GyroZ = value.GyroZ,
            HasGyro = value.HasGyro,
            AccelX = value.AccelX,
            AccelY = value.AccelY,
            AccelZ = value.AccelZ,
            HasAccelerometer = value.HasAccelerometer,
            SensorTimestamp = value.SensorTimestamp
        };
        return result;
    }

    private static P.MotionSample ToLibrary(MotionSample value)
    {
        var result = new P.MotionSample
        {
            GyroX = value.GyroX,
            GyroY = value.GyroY,
            GyroZ = value.GyroZ,
            HasGyro = value.HasGyro,
            AccelX = value.AccelX,
            AccelY = value.AccelY,
            AccelZ = value.AccelZ,
            HasAccelerometer = value.HasAccelerometer,
            SensorTimestamp = value.SensorTimestamp
        };
        return result;
    }

    private static HapticOutputFrame ToSdk(P.HapticOutputFrame value)
    {
        var result = new HapticOutputFrame
        {
            LowFrequency = value.LowFrequency,
            HighFrequency = value.HighFrequency,
            LeftTrigger = value.LeftTrigger,
            RightTrigger = value.RightTrigger,
            Timestamp = value.Timestamp
        };
        return result;
    }

    private static P.HapticOutputFrame ToLibrary(HapticOutputFrame value)
    {
        var result = new P.HapticOutputFrame
        {
            LowFrequency = value.LowFrequency,
            HighFrequency = value.HighFrequency,
            LeftTrigger = value.LeftTrigger,
            RightTrigger = value.RightTrigger,
            Timestamp = value.Timestamp
        };
        return result;
    }

    private static HapticCapabilities ToSdk(P.HapticCapabilities value)
    {
        var result = new HapticCapabilities
        {
            LowFrequency = (OutputChannelSupport)value.LowFrequency,
            HighFrequency = (OutputChannelSupport)value.HighFrequency,
            LeftTrigger = (OutputChannelSupport)value.LeftTrigger,
            RightTrigger = (OutputChannelSupport)value.RightTrigger,
            MaxFramesPerSecond = value.MaxFramesPerSecond,
            MinimumStartIntensity = value.MinimumStartIntensity,
            MinimumPulse = value.MinimumPulse
        };
        return result;
    }

    private static P.HapticCapabilities ToLibrary(HapticCapabilities value)
    {
        var result = new P.HapticCapabilities
        {
            LowFrequency = (P.OutputChannelSupport)value.LowFrequency,
            HighFrequency = (P.OutputChannelSupport)value.HighFrequency,
            LeftTrigger = (P.OutputChannelSupport)value.LeftTrigger,
            RightTrigger = (P.OutputChannelSupport)value.RightTrigger,
            MaxFramesPerSecond = value.MaxFramesPerSecond,
            MinimumStartIntensity = value.MinimumStartIntensity,
            MinimumPulse = value.MinimumPulse
        };
        return result;
    }

    private static PhysicalDeviceIdentity ToSdk(P.PhysicalDeviceIdentity value)
    {
        var result = new PhysicalDeviceIdentity
        {
            InstancePath = value.InstancePath,
            LocationPath = value.LocationPath,
            VendorId = value.VendorId,
            ProductId = value.ProductId,
            RequiresHiding = value.RequiresHiding
        };
        return result;
    }

    private static P.PhysicalDeviceIdentity ToLibrary(PhysicalDeviceIdentity value)
    {
        var result = new P.PhysicalDeviceIdentity
        {
            InstancePath = value.InstancePath,
            LocationPath = value.LocationPath,
            VendorId = value.VendorId,
            ProductId = value.ProductId,
            RequiresHiding = value.RequiresHiding
        };
        return result;
    }

    private static OemControlDescriptor ToSdk(P.OemControlDescriptor value)
    {
        var result = new OemControlDescriptor
        {
            ControlId = value.ControlId,
            Placement = (OemControlPlacement)value.Placement,
            SupportsLongPress = value.SupportsLongPress,
            RequiresControllerAcquisition = value.RequiresControllerAcquisition,
            CompanionApplication = value.CompanionApplication,
            Display = Display(value.Label)
        };
        return result;
    }

    private static DeviceIdentitySnapshot ToSdk(P.DeviceIdentitySnapshot value)
    {
        var result = new DeviceIdentitySnapshot
        {
            SystemManufacturer = value.SystemManufacturer,
            SystemProduct = value.SystemProduct,
            SystemSku = value.SystemSku,
            SystemFamily = value.SystemFamily,
            BaseboardManufacturer = value.BaseboardManufacturer,
            BaseboardProduct = value.BaseboardProduct,
            BaseboardVersion = value.BaseboardVersion,
            BiosVersion = value.BiosVersion,
            EcFirmwareVersion = value.EcFirmwareVersion,
            McuFirmwareVersion = value.McuFirmwareVersion,
            CpuIdentity = value.CpuIdentity,
            ProcessorName = value.ProcessorName,
            UsbEndpoints = Array.AsReadOnly(value.UsbEndpoints.Select(ToSdk).ToArray()),
            WmiProviderSignatures = Array.AsReadOnly(value.WmiProviderSignatures.ToArray())
        };
        return result;
    }

    private static P.DeviceIdentitySnapshot ToLibrary(DeviceIdentitySnapshot value)
    {
        var result = new P.DeviceIdentitySnapshot
        {
            SystemManufacturer = value.SystemManufacturer,
            SystemProduct = value.SystemProduct,
            SystemSku = value.SystemSku,
            SystemFamily = value.SystemFamily,
            BaseboardManufacturer = value.BaseboardManufacturer,
            BaseboardProduct = value.BaseboardProduct,
            BaseboardVersion = value.BaseboardVersion,
            BiosVersion = value.BiosVersion,
            EcFirmwareVersion = value.EcFirmwareVersion,
            McuFirmwareVersion = value.McuFirmwareVersion,
            CpuIdentity = value.CpuIdentity,
            ProcessorName = value.ProcessorName,
            UsbEndpoints = Array.AsReadOnly(value.UsbEndpoints.Select(ToLibrary).ToArray()),
            WmiProviderSignatures = Array.AsReadOnly(value.WmiProviderSignatures.ToArray())
        };
        return result;
    }

    private static UsbEndpointObservation ToSdk(P.UsbEndpointObservation value)
    {
        var result = new UsbEndpointObservation
        {
            VendorId = value.VendorId,
            ProductId = value.ProductId,
            InterfaceNumber = value.InterfaceNumber,
            DeviceRelease = value.DeviceRelease,
            ReportDescriptorHash = value.ReportDescriptorHash,
            ReportLengths = Array.AsReadOnly(value.ReportLengths.ToArray()),
            LocationPath = value.LocationPath
        };
        return result;
    }

    private static P.UsbEndpointObservation ToLibrary(UsbEndpointObservation value)
    {
        var result = new P.UsbEndpointObservation
        {
            VendorId = value.VendorId,
            ProductId = value.ProductId,
            InterfaceNumber = value.InterfaceNumber,
            DeviceRelease = value.DeviceRelease,
            ReportDescriptorHash = value.ReportDescriptorHash,
            ReportLengths = Array.AsReadOnly(value.ReportLengths.ToArray()),
            LocationPath = value.LocationPath
        };
        return result;
    }

    private static CapabilityReason ToSdk(P.CapabilityReason value)
    {
        return new CapabilityReason((CapabilityReasonCode)value.Code, value.Detail, value.Retryable);
    }

    private static P.CapabilityReason ToLibrary(CapabilityReason value)
    {
        return new P.CapabilityReason((P.CapabilityReasonCode)value.Code, value.Detail, value.Retryable);
    }

    private static CurvePoint ToSdk(P.CurvePoint value)
    {
        return new CurvePoint(value.Input, value.Output);
    }

    private static P.CurvePoint ToLibrary(CurvePoint value)
    {
        return new P.CurvePoint(value.Input, value.Output);
    }

    private static CapabilityChoice ToSdk(P.CapabilityChoice value)
    {
        return new CapabilityChoice(value.Value, Display(value.Label));
    }

    private static DevicePowerPreset ToSdk(P.DevicePowerPreset value)
    {
        return new DevicePowerPreset(value.Id, value.Name, value.SustainedWatts, value.SlowWatts,
                (DevicePowerMode)value.WindowsMode)
            { ScenarioOnAc = value.ScenarioOnAc, ScenarioOnDc = value.ScenarioOnDc };
    }

    private static P.DevicePowerPreset ToLibrary(DevicePowerPreset value)
    {
        return new P.DevicePowerPreset(value.Id, value.Name, value.SustainedWatts, value.SlowWatts,
                (P.DevicePowerMode)value.WindowsMode)
            { ScenarioOnAc = value.ScenarioOnAc, ScenarioOnDc = value.ScenarioOnDc };
    }

    private static OemControlEvent ToSdk(P.OemControlEvent value)
    {
        return new OemControlEvent(value.ControlId, (OemPressKind)value.Press, value.Timestamp, value.DeduplicationId,
            (OemControlEdge)value.Edge);
    }

    private static P.OemControlEvent ToLibrary(OemControlEvent value)
    {
        return new P.OemControlEvent(value.ControlId, (P.OemPressKind)value.Press, value.Timestamp,
            value.DeduplicationId, (P.OemControlEdge)value.Edge);
    }
}

internal sealed partial class HandheldDeviceAdapter
{
    private readonly HandheldDevice _device;
    private IPluginHostAdapter? _host;

    internal HandheldDeviceAdapter(P.HandheldDefinition definition, string stateDirectory)
    {
        _device = HandheldDevice.Create(definition, stateDirectory, Trace);
        _device.Observer = this;
    }

    internal P.HandheldDefinition Definition => _device.Definition;
    internal IReadOnlyList<string> GlyphResources => _device.GlyphResources;

    // Presentation belongs to WSGM. The neutral native contract carries only roles, bounds and plain names.
    private static IReadOnlyList<CapabilitySection> Sections { get; } =
    [
        DeviceSections.Power with
        {
            Categories =
            [Category("limits", "Limits", 0), Category("charging", "Charging", 1), Category("fans", "Fans", 2)]
        },
        DeviceSections.Rgb with { Categories = [Category("zones", "Zones", 0)] },
        DeviceSections.Info with
        {
            Categories = [Category("ownership", "Device ownership", 0), Category("readings", "Readings", 1)]
        }
    ];

    public string PackageId => Definition.FamilyId;

    public ValueTask<PluginDetectionResult> DetectAsync(PluginDetectionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var match = Detect(context.Identity);
        return ValueTask.FromResult(new PluginDetectionResult
            { Matched = match?.Id == Definition.Id, DeviceDefinitionId = match?.Id });
    }

    public async ValueTask<PluginStartResult> StartAsync(PluginStartContext context,
        CancellationToken cancellationToken)
    {
        if (context.DeviceDefinitionId != Definition.Id)
        {
            throw new ArgumentException("Definition does not match this native engine.", nameof(context));
        }

        _host = context.Host;
        return StartResult(await _device
            .StartAsync(context.CycleGeneration, context.ControllerManagementEnabled, cancellationToken)
            .ConfigureAwait(false));
    }

    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        return ToSdk(await _device.ExecuteAsync(ToLibrary(command), cancellationToken).ConfigureAwait(false));
    }

    public ValueTask ApplySettingsAsync(IReadOnlyList<DeviceSettingValue> values, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (values.Count != 0)
        {
            throw new ArgumentException("These native families declare no preferences.", nameof(values));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken cancellationToken)
    {
        return _device.SuspendAsync(Budget(context.Deadline), cancellationToken);
    }

    public async ValueTask<PluginStartResult> ResumeAsync(PluginResumeContext context,
        CancellationToken cancellationToken)
    {
        return StartResult(await _device
            .ResumeAsync(context.CycleGeneration, Budget(context.Deadline), cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        return new PluginDiagnostics
            { Values = await _device.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false) };
    }

    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        return _device.ApplyHapticsAsync(ToLibrary(frame), cancellationToken);
    }

    public ValueTask ReleaseControllerAsync(PluginControllerReleaseContext context, CancellationToken cancellationToken)
    {
        return _device.ReleaseControllerAsync((P.HandoffScope)context.Scope, Budget(context.Deadline),
            cancellationToken);
    }

    public ValueTask SetControllerManagementAsync(PluginControllerManagementContext context,
        CancellationToken cancellationToken)
    {
        return _device.SetControllerManagementAsync(context.Enabled, Budget(context.Deadline), cancellationToken);
    }

    public async ValueTask<PluginStopResult> StopAsync(PluginStopContext context, CancellationToken cancellationToken)
    {
        var result = await _device
            .StopAsync((P.DeviceStopReason)context.Reason, Budget(context.Deadline), cancellationToken)
            .ConfigureAwait(false);
        return new PluginStopResult
            { Status = (PluginStopStatus)result.Status, Reason = result.Reason is { } reason ? ToSdk(reason) : null };
    }

    public ValueTask DisposeAsync()
    {
        return _device.DisposeAsync();
    }

    ValueTask P.IHandheldObserver.DescriptorsChangedAsync(P.CapabilityDescriptorSet descriptors,
        CancellationToken cancellationToken)
    {
        return _host?.PublishDescriptorsAsync(ToSdk(descriptors), cancellationToken) ?? ValueTask.CompletedTask;
    }

    ValueTask P.IHandheldObserver.StateChangedAsync(P.CapabilityState state, CancellationToken cancellationToken)
    {
        return _host?.PublishCapabilityStateAsync(ToSdk(state), cancellationToken) ?? ValueTask.CompletedTask;
    }

    ValueTask P.IHandheldObserver.PhysicalDevicesChangedAsync(IReadOnlyList<P.PhysicalDeviceIdentity> devices,
        P.HapticCapabilities? output, CancellationToken cancellationToken)
    {
        return _host?.PublishPhysicalDevicesAsync(Array.AsReadOnly(devices.Select(ToSdk).ToArray()),
            output is null ? null : ToSdk(output), cancellationToken) ?? ValueTask.CompletedTask;
    }

    ValueTask P.IHandheldObserver.ControllerSampleAsync(P.CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        return _host?.PublishControllerSampleAsync(ToSdk(sample), cancellationToken) ?? ValueTask.CompletedTask;
    }

    ValueTask P.IHandheldObserver.OemControlsChangedAsync(IReadOnlyList<P.OemControlDescriptor> controls,
        CancellationToken cancellationToken)
    {
        return _host?.PublishOemControlsAsync(Array.AsReadOnly(controls.Select(ToSdk).ToArray()), cancellationToken) ??
               ValueTask.CompletedTask;
    }

    ValueTask P.IHandheldObserver.OemEventAsync(P.OemControlEvent controlEvent, CancellationToken cancellationToken)
    {
        return _host?.PublishOemEventAsync(ToSdk(controlEvent), cancellationToken) ?? ValueTask.CompletedTask;
    }

    void P.IHandheldObserver.Faulted(string scope, string message)
    {
        _host?.ReportFault(scope, message);
    }

    internal static P.HandheldDefinition? Detect(DeviceIdentitySnapshot identity)
    {
        return HandheldDevice.Detect(ToLibrary(identity));
    }

    internal Stream OpenGlyphResource(string relativePath)
    {
        return _device.OpenGlyphResource(relativePath);
    }

    private static P.Deadline Budget(Deadline deadline)
    {
        return P.Deadline.After(deadline.Remaining);
    }

    private static PluginStartResult StartResult(P.DeviceStartResult result)
    {
        return new PluginStartResult
        {
            State = (PluginOperationalState)result.State, Reason = result.Reason is { } reason ? ToSdk(reason) : null
        };
    }

    private void Trace(P.DeviceLogEntry entry)
    {
        if (entry.ChangeKey is { } key)
        {
            _host?.TraceChange((DeviceTraceLevel)entry.Level, entry.Scope, key, entry.Message);
        }
        else
        {
            _host?.Trace((DeviceTraceLevel)entry.Level, entry.Scope, entry.Message);
        }
    }

    private static CapabilityCategory Category(string id, string title, int order)
    {
        return new CapabilityCategory
            { CategoryId = id, Key = SettingSectionKey.Custom, CustomTitle = title, SortOrder = order };
    }

    private static CapabilityDescriptor Place(CapabilityDescriptor value)
    {
        var (section, category, order) = value.Role switch
        {
            CapabilityRole.PowerSustainedLimit => (DeviceSections.PowerId, "limits", 0),
            CapabilityRole.PowerSlowLimit => (DeviceSections.PowerId, "limits", 1),
            CapabilityRole.PowerFastLimit => (DeviceSections.PowerId, "limits", 2),
            CapabilityRole.PowerPeakLimit => (DeviceSections.PowerId, "limits", 3),
            CapabilityRole.ScenarioMode => (DeviceSections.PowerId, "limits", 4),
            CapabilityRole.ChargeLimit or CapabilityRole.ChargeProtectionMode or CapabilityRole.ChargeBypass => (
                DeviceSections.PowerId, "charging", 0),
            CapabilityRole.FanMode => (DeviceSections.PowerId, "fans", 0),
            CapabilityRole.FanCurve => (DeviceSections.PowerId, "fans", 1),
            CapabilityRole.FanDuty or CapabilityRole.FanTargetRpm => (DeviceSections.PowerId, "fans", 2),
            CapabilityRole.LightingPower => (DeviceSections.RgbId, null, 0),
            CapabilityRole.LightingBrightness => (DeviceSections.RgbId, null, 1),
            CapabilityRole.LightingEffect => (DeviceSections.RgbId, null, 2),
            CapabilityRole.LightingEffectSpeed => (DeviceSections.RgbId, null, 3),
            CapabilityRole.LightingZoneColor => (DeviceSections.RgbId, "zones",
                value.InstanceId is "right" or "right-ring" ? 1 : value.InstanceId == "buttons" ? 2 : 0),
            CapabilityRole.ControllerSource => (DeviceSections.InfoId, "ownership", 0),
            CapabilityRole.MotionSource => (DeviceSections.InfoId, "ownership", 1),
            CapabilityRole.HapticSink => (DeviceSections.InfoId, "ownership", 2),
            _ => (DeviceSections.InfoId, "readings",
                value.InstanceId is "right" or "gpu" ? 2 : value.InstanceId is "left" or "cpu" ? 1 : 0)
        };
        return value with
        {
            SectionId = section, CategoryId = category, SortOrder = order,
            Prominence = value.Role == CapabilityRole.PowerSustainedLimit ? CapabilityProminence.Primary :
            value.SupportsWrite ? CapabilityProminence.Normal : CapabilityProminence.Compact,
            LayoutPair = value.Role == CapabilityRole.PowerSustainedLimit && value.PairedPowerLimitId is { } pair
                ? new CapabilityLayoutPair(pair)
                : null
        };
    }

    private static CapabilityDisplay Display(string label)
    {
        return label switch
        {
            "TDP" => new CapabilityDisplay { Key = DisplayKey.Tdp },
            "Sustained power limit" => new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
            "Boost power limit" => new CapabilityDisplay { Key = DisplayKey.BoostPowerLimit },
            "Performance profile" => new CapabilityDisplay { Key = DisplayKey.PerformanceProfile },
            "Fan mode" => new CapabilityDisplay { Key = DisplayKey.FanMode },
            "Fan speed" => new CapabilityDisplay { Key = DisplayKey.FanSpeed },
            "Fan curve" => new CapabilityDisplay { Key = DisplayKey.FanCurve },
            "Left fan" => new CapabilityDisplay { Key = DisplayKey.FanLeft },
            "Right fan" => new CapabilityDisplay { Key = DisplayKey.FanRight },
            "Charge limit" => new CapabilityDisplay { Key = DisplayKey.ChargeLimit },
            "Bypass charging" => new CapabilityDisplay { Key = DisplayKey.BypassCharging },
            "Lighting" => new CapabilityDisplay { Key = DisplayKey.Lighting },
            "Brightness" => new CapabilityDisplay { Key = DisplayKey.Brightness },
            "Effect" => new CapabilityDisplay { Key = DisplayKey.LightingEffect },
            "Effect speed" => new CapabilityDisplay { Key = DisplayKey.LightingEffectSpeed },
            "CPU temperature" => new CapabilityDisplay { Key = DisplayKey.CpuTemperature },
            "Battery" => new CapabilityDisplay { Key = DisplayKey.Battery },
            "Controller" => new CapabilityDisplay { Key = DisplayKey.Controller },
            "Motion" => new CapabilityDisplay { Key = DisplayKey.Motion },
            "Rumble" => new CapabilityDisplay { Key = DisplayKey.Rumble },
            "Variable refresh rate" => new CapabilityDisplay { Key = DisplayKey.VariableRefreshRate },
            _ => new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label }
        };
    }
}
