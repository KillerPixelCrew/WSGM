using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Install;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>
///     The session's plugin and device integration set: the plugin host, the graphics packages'
///     capability router with WSGM's built-in GPU drivers, the common plugin manager and the device
///     coordinator with its AutoTDP. Built off the UI thread before any surface exists.
/// </summary>
/// <remarks>
///     Overlay-test creates none of these owners: it discovers no packages and loads no plugin code,
///     and its Device and Graphics destinations get simulated publications instead.
/// </remarks>
internal sealed class IntegrationServices
{
    private readonly SessionState _state;

    /// <summary>Creates the set's plugin host; nothing is discovered or started until <see cref="StartPlugins" />.</summary>
    /// <param name="state">The session's live state.</param>
    internal IntegrationServices(SessionState state)
    {
        _state = state;
        PluginHost = new PluginHost(action => Dispatcher.UIThread.Post(action),
            new ApplicationPluginConfigurationStore(state.Store));
    }

    /// <summary>The one plugin host of the session.</summary>
    internal PluginHost PluginHost { get; }

    /// <summary>The graphics packages' capability owner, created with the common plugin manager.</summary>
    internal GpuCoordinator? Gpu { get; private set; }

    /// <summary>WSGM's built-in GPU drivers over <see cref="Gpu" />.</summary>
    internal BuiltinGpuService? BuiltinGpu { get; private set; }

    /// <summary>The scaled resolutions the built-in drivers publish.</summary>
    internal ScaledResolutionService? ScaledResolutions { get; private set; }

    /// <summary>The common plugin manager, or null in overlay-test or when it could not start.</summary>
    internal CommonPluginManager? CommonPlugins { get; private set; }

    /// <summary>The first common plugin reconcile, which desktop and entry actions wait for.</summary>
    internal Task CommonPluginStartup { get; private set; } = Task.CompletedTask;

    /// <summary>The adopted device coordinator, or null without an exact handheld or with integration off.</summary>
    internal DeviceCoordinator? Device { get; private set; }

    /// <summary>The coordinator's AutoTDP, which shares its power lane.</summary>
    internal AutoTdpService? AutoTdp { get; private set; }

    /// <summary>The overlay's Device destination source: the coordinator's, or the simulated one in overlay-test.</summary>
    internal IDeviceOverlaySource? DeviceOverlay { get; private set; }

    /// <summary>The overlay's Graphics destination source: the coordinator's, or the simulated one in overlay-test.</summary>
    internal IGraphicsOverlaySource? GraphicsOverlay { get; private set; }

    /// <summary>Whether any owner a device power transition reaches exists.</summary>
    internal bool HasPowerOwners => Device is not null || CommonPlugins is not null || BuiltinGpu is not null;

    /// <summary>Applies the Device Integration master switch to AutoTDP at every entry point.</summary>
    /// <param name="config">Current device integration switches.</param>
    /// <returns>True only when both device integration and AutoTDP are enabled.</returns>
    internal static bool ShouldRunAutoTdp(DeviceIntegrationConfig config)
    {
        return config is { Enabled: true, AutoTdpEnabled: true };
    }

    /// <summary>
    ///     Removes package files the Plugins page queued, then creates the graphics router, the built-in
    ///     drivers and the common plugin manager, and starts the first plugin reconcile. Off the UI thread.
    /// </summary>
    /// <param name="profiles">The profile owner graphics values are saved through.</param>
    /// <param name="persistManualVariableRefresh">Saves variable refresh set on a graphics package's control.</param>
    internal void StartPlugins(ProfileService profiles, Action<bool> persistManualVariableRefresh)
    {
        // Package files the Plugins page removed while they were loaded go now, once, before the
        // common plugins or the device integration open any package, whatever the integration
        // switch says.
        new PendingPluginRemovalStore(InstallLayout.PendingPluginRemovals).Apply(InstallLayout.Plugins);

        // Installed packages only. WSGM bundles none, and the application directory is
        // user-writable, so scanning it would load plugin code from a path the installed
        // root is administrator-protected precisely to avoid.
        // Graphics packages publish capabilities through their own owner, created first so the
        // channel of every graphics package the manager starts has a router waiting for it.
        // Variable refresh set on a graphics package's control is saved as the device's is.
        ShellSession.TryStart("graphics capability router", () =>
            Gpu = new GpuCoordinator(action => Dispatcher.UIThread.Post(action), profiles, PluginHost,
                PowerSource.ReadOnAc, persistManualVariableRefresh));
        if (Gpu is { } graphics)
        {
            BuiltinGpu = new BuiltinGpuService(_state.Store, graphics);
            ScaledResolutions = new ScaledResolutionService(_state.Store, BuiltinGpu);
        }

        ShellSession.TryStart("common plugin manager", () =>
            CommonPlugins = new CommonPluginManager(PluginHost, InstallLayout.Plugins,
                Path.Combine(_state.Store.Context.Root, "PluginState"), capabilityChannels: Gpu,
                store: _state.Store));
        CommonPluginStartup = ApplyPluginConfigAsync(_state.Config);
    }

    /// <summary>Starts the device coordinator off the UI thread; the caller adopts or releases it.</summary>
    /// <param name="profiles">The profile owner every per-game value is read from and written to.</param>
    /// <param name="performance">The performance set AutoTDP reads its frame deadline and sensors from.</param>
    /// <param name="persistManualVariableRefresh">Saves variable refresh set on the Device row or Steam's control.</param>
    /// <param name="powerModes">The session's Windows power-mode owner.</param>
    /// <returns>The unadopted coordinator, or null when none started.</returns>
    internal Task<DeviceCoordinator?> StartDeviceAsync(ProfileService profiles, PerformanceServices performance,
        Action<bool> persistManualVariableRefresh, WindowsPowerModes powerModes)
    {
        return DeviceCoordinator.TryStartAsync(
            _state.Config, _state.Store,
            profiles,
            performance.TargetFrametimeMs,
            performance.SampleSensors,
            // Variable refresh set on the Device row or Steam's control is saved to the profile.
            persistManualVariableRefresh,
            powerModes,
            _state.ShutdownToken);
    }

    /// <summary>Makes a started coordinator the session's own. UI thread, before any surface is built.</summary>
    /// <param name="coordinator">The coordinator, or null when none started.</param>
    internal void AdoptDevice(DeviceCoordinator? coordinator)
    {
        Device = coordinator;
    }

    /// <summary>Takes the coordinator's AutoTDP and creates the overlay's Device source, or the simulated one.</summary>
    internal void StartDeviceOverlay()
    {
        if (_state.OverlayTestOnly)
        {
            DeviceOverlay = new SimulatedDeviceOverlaySource();
            return;
        }

        if (Device is not { } deviceCoordinator)
        {
            return;
        }

        // The coordinator owns AutoTDP: its writes share the coordinator's power lane, a hand-set
        // limit pauses it and is saved from the coordinator's manual funnel, and its original limit
        // is restored before the device stops.
        AutoTdp = deviceCoordinator.Power.AutoTdp;
        AutoTdp.SetTraceEnabled(_state.Config.AutoTdpTraceEnabled);
        DeviceOverlay = new DeviceOverlayBridge(deviceCoordinator, Gpu);
    }

    /// <summary>Creates the overlay's Graphics source over the graphics packages, or the simulated one.</summary>
    internal void StartGraphicsOverlay()
    {
        // Graphics follows the graphics packages, not the device integration switch. Overlay-test loads no
        // plugin, so it gets an in-memory publication to try the destination with.
        GraphicsOverlay = Gpu is { } gpu
            ? new GraphicsOverlayBridge(gpu)
            : _state.OverlayTestOnly
                ? new SimulatedGraphicsOverlaySource()
                : null;
    }

    /// <summary>
    ///     Creates the overlay's plugin source, with the device's widgets when a device runs. Null when
    ///     neither the common plugins nor the device exist.
    /// </summary>
    /// <returns>The source, which Settings' action lists read too.</returns>
    internal CommonPluginOverlaySource? StartPluginOverlay()
    {
        return PluginOverlay = CommonPlugins is null && Device is null
            ? null
            : new CommonPluginOverlaySource(_state.Store, CommonPlugins, PluginHost, _state.Config.PluginWidgetPins,
                Device is not null && DeviceOverlay is not null
                    ? new DeviceWidgetSource(Device, DeviceOverlay)
                    : null);
    }

    /// <summary>The overlay's plugin source, or null.</summary>
    internal CommonPluginOverlaySource? PluginOverlay { get; private set; }

    /// <summary>
    ///     Every action the running non-device instances declare, for the Settings lists.
    ///     Device instances are excluded: their controls belong to the Device surfaces, and a session
    ///     automation step reaching into hardware policy would be a second owner for it.
    /// </summary>
    /// <returns>The actions, labelled for a list.</returns>
    internal IReadOnlyList<PluginActionOption> ReadPluginActionOptions()
    {
        if (PluginOverlay is not { } source)
        {
            return [];
        }

        PluginInstanceIdentity[] devices =
            [.. source.Device?.Snapshot().Select(instance => instance.Identity) ?? []];
        return
        [
            .. source.Snapshot()
                .Where(instance => !Array.Exists(devices, device => device == instance.Identity)
                                   && instance.Controls is not null)
                .SelectMany(instance => instance.Controls!.Actions.Select(action =>
                    new PluginActionOption(instance.Identity, action,
                        $"{instance.Name} / {instance.Identity.InstanceId}: {action.Label}")))
        ];
    }

    /// <summary>
    ///     Optional component availability for the already detected exact handheld.
    ///     An integration the user disabled is quiet and opens no device or driver handles here.
    /// </summary>
    /// <returns>What the overlay's prerequisites banner shows.</returns>
    internal DevicePrerequisiteState ReadDevicePrerequisiteState()
    {
        var device = Device?.DeviceDefinition;
        var config = _state.Config;
        if (!config.DeviceIntegration.Enabled || device is null)
        {
            return new DevicePrerequisiteState(device is not null, config.DeviceIntegration.Enabled, false, false, []);
        }

        return new DevicePrerequisiteState(
            HandheldSupported: true,
            IntegrationEnabled: true,
            DevicePrerequisiteSource.ControllerLibraryInstalled(AppContext.BaseDirectory),
            DevicePrerequisiteSource.HidHideInstalled(),
            SetupComponents.Required(device),
            DevicePrerequisiteSource.HardwareDriverInstalled("PawnIO"),
            DevicePrerequisiteSource.HardwareDriverInstalled("inpoutx64"));
    }

    /// <summary>The profile keys of the device and every graphics package running now.</summary>
    /// <returns>The keys stored values of a running package carry.</returns>
    internal IReadOnlyCollection<string> LiveCapabilityPublishers()
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        if (Device?.DeviceIdentityKey is { } device)
        {
            keys.Add(device);
        }

        foreach (var key in Gpu?.ActiveProfileKeys ?? [])
        {
            keys.Add(key);
        }

        return keys;
    }

    /// <summary>Applies a reloaded configuration to the plugins, AutoTDP and the device, in that order.</summary>
    /// <param name="config">The reloaded configuration.</param>
    /// <param name="deviceApplied">Runs once the coordinator has applied it.</param>
    internal void ApplyConfig(AppConfig config, Action deviceApplied)
    {
        _ = ApplyPluginConfigAsync(config);
        var coordinator = Device;
        if (coordinator is null)
        {
            return;
        }

        // AutoTDP is applied before the coordinator: turning Device Integration off must stop
        // AutoTDP and restore the previous power limit while the capability is still writable.
        AutoTdp?.SetTraceEnabled(config.AutoTdpTraceEnabled);
        AutoTdp?.Apply(ShouldRunAutoTdp(config.DeviceIntegration));
        Log.Observe(ApplyDeviceConfigAsync(coordinator, config, deviceApplied), "Device cycle config apply", true);
    }

    private static async Task ApplyDeviceConfigAsync(DeviceCoordinator coordinator, AppConfig config,
        Action deviceApplied)
    {
        await coordinator.ApplyConfigAsync(config).ConfigureAwait(false);
        deviceApplied();
    }

    private async Task ApplyGraphicsConfigAsync(AppConfig config)
    {
        try
        {
            if (BuiltinGpu is { } graphics)
            {
                await graphics.ReconcileAsync(config, _state.ShutdownToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_state.ShutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Graphics configuration failed", ex);
        }
    }

    private async Task ApplyPluginConfigAsync(AppConfig config)
    {
        var graphicsWork = ApplyGraphicsConfigAsync(config);
        try
        {
            if (CommonPlugins is { } manager)
            {
                // Exactly what the user enabled. Nothing is admitted implicitly: the auto-enable pass
                // existed for bundled packages, and WSGM bundles none.
                await manager.ReconcileAsync(config.PluginInstances, _state.ShutdownToken,
                        config.SteamCefPluginWarningAccepted)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_state.ShutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Common plugin configuration failed", ex);
        }

        await graphicsWork.ConfigureAwait(false);
    }

    /// <summary>Re-reads the graphics adapters after the display topology changed.</summary>
    internal void RefreshGraphicsTopology()
    {
        if (BuiltinGpu is { } drivers)
        {
            Log.Observe(drivers.RefreshTopologyAsync(_state.Config, _state.ShutdownToken),
                "Graphics adapter topology refresh", true);
        }
    }

    /// <summary>Restores driver values through the existing graphics owner without requiring GPU startup for panic recovery.</summary>
    /// <param name="preferences">The recorded driver values.</param>
    /// <param name="cancellationToken">Stops waiting for the restore.</param>
    /// <returns>Whether every value was accepted.</returns>
    internal async Task<bool> RestorePendingDisplayGpuAsync(IReadOnlyList<DisplayGpuPreference> preferences,
        CancellationToken cancellationToken)
    {
        var warning = await DisplayGpuProfileService.ApplyAsync(Gpu, preferences, cancellationToken)
            .ConfigureAwait(false);
        if (warning is not null)
        {
            Log.Warn("Desktop display controls: " + warning);
        }

        return warning is null;
    }

    /// <summary>Tells every plugin which shell mode the session is in, within five seconds.</summary>
    /// <param name="mode">The mode now in force.</param>
    /// <returns>Completion of the notification, which never faults.</returns>
    internal async Task NotifyModeAsync(PluginSessionMode mode)
    {
        try
        {
            await PluginHost.SetModeAsync(mode, Deadline.After(TimeSpan.FromSeconds(5)), _state.ShutdownToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_state.ShutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Plugin mode transition did not complete", ex);
        }
    }

    /// <summary>Creates a sequence that runs configured plugin actions through the host.</summary>
    /// <returns>A new sequence.</returns>
    internal PluginActionSequence ActionSequence()
    {
        return new PluginActionSequence(new PluginHostActionInvoker(PluginHost), Log.Info);
    }

    /// <summary>Carries a device power transition to the built-in drivers and the common plugins.</summary>
    /// <param name="suspend">Whether the session is suspending.</param>
    /// <returns>Completion of both, which never faults.</returns>
    internal async Task PluginPowerTransitionAsync(bool suspend)
    {
        try
        {
            if (BuiltinGpu is { } graphics)
            {
                await graphics.PowerTransitionAsync(suspend, _state.ShutdownToken).ConfigureAwait(false);
            }

            if (CommonPlugins is { } manager)
            {
                await manager.PowerTransitionAsync(suspend, _state.ShutdownToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_state.ShutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Common plugin power transition failed", ex);
        }
    }

    /// <summary>Closes the device, common plugin, GPU driver and graphics admissions, in that order.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void CloseAdmission(ShutdownSteps steps)
    {
        steps.Step("Closing device admission failed", () => Device?.CloseAdmission());
        steps.Step("Closing common plugin admission failed", () => CommonPlugins?.CloseAdmission());
        steps.Step("Closing GPU driver admission failed", () => BuiltinGpu?.CloseAdmission());
        steps.Step("Closing graphics admission failed", () => Gpu?.CloseAdmission());
    }

    /// <summary>
    ///     Stops the device: the coordinator restores AutoTDP's original limit through its still open
    ///     capability path, then releases the controller, shows the pad again and stops the device.
    /// </summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <param name="reason">The application's shutdown reason.</param>
    /// <returns>Completion of the step, which never faults.</returns>
    internal async Task ShutdownDeviceAsync(ShutdownSteps steps, ApplicationShutdownReason reason)
    {
        AutoTdp = null;
        if (Device is not { } coordinator)
        {
            return;
        }

        var deviceReason = DeviceShutdownReason(reason);
        Device = null;
        await steps.StepAsync("Device cleanup was unverified; remaining shell cleanup continues",
                () => coordinator.ShutdownAsync(deviceReason, Deadline.At(steps.Deadline)).AsTask())
            .ConfigureAwait(false);
    }

    /// <summary>Stops the common plugins, then the GPU drivers and the graphics router, so no graphics channel is open when it goes.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <returns>Completion of the steps, which never fault.</returns>
    internal async Task StopPluginsAsync(ShutdownSteps steps)
    {
        if (CommonPlugins is { } commonPlugins)
        {
            await steps.StepAsync("Common plugin cleanup was unconfirmed; remaining shell cleanup continues",
                () => commonPlugins.StopAsync(Deadline.At(steps.Deadline))).ConfigureAwait(false);
        }

        if (Gpu is { } gpu)
        {
            if (BuiltinGpu is { } drivers)
            {
                await steps.StepAsync("GPU driver cleanup was unconfirmed",
                    () => drivers.StopAsync(Deadline.At(steps.Deadline))).ConfigureAwait(false);
            }

            await steps.StepAsync("Graphics capability cleanup failed",
                () => gpu.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes the overlay's Device and Graphics sources on the UI thread that built them.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void DisposeOverlaySources(ShutdownSteps steps)
    {
        var deviceOverlay = DeviceOverlay;
        DeviceOverlay = null;
        steps.Step("Disposing the device overlay source failed", () => deviceOverlay?.Dispose());
        var graphicsOverlay = GraphicsOverlay;
        GraphicsOverlay = null;
        steps.Step("Disposing the graphics overlay source failed", () => graphicsOverlay?.Dispose());
    }

    /// <summary>The device protocol reason for an application shutdown reason.</summary>
    /// <param name="reason">The application's shutdown reason.</param>
    /// <returns>The reason the handheld is told.</returns>
    internal static HandheldStopReason DeviceShutdownReason(ApplicationShutdownReason reason)
    {
        return ApplicationShutdownRequest.SessionEnding
            ? HandheldStopReason.SessionEnding
            : reason switch
            {
                ApplicationShutdownReason.Update => HandheldStopReason.Updating,
                ApplicationShutdownReason.SessionEnd => HandheldStopReason.SessionEnding,
                ApplicationShutdownReason.Uninstall => HandheldStopReason.Uninstalling,
                _ => HandheldStopReason.WsgmExiting
            };
    }
}
