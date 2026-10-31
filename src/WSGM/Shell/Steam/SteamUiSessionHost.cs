using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Owns the narrow bridge and registered patches over the injected process-long Steam UI transport.
/// </summary>
/// <remarks>
///     Session lifetime only: which patches are applied when, generation changes, synchronization, and
///     publication gating. The surfaces themselves — what each gate installs, which rows mount, how a
///     payload is read — are the toolkit's; <see cref="SteamUiModuleCatalog" /> declares them over the
///     backend services, and <see cref="SteamUiPatchSwitches" /> derives which are on.
/// </remarks>
internal sealed class SteamUiSessionHost : IAsyncDisposable
{
    /// <summary>
    ///     The session-owned services behind the surfaces. The session builds and disposes them; this
    ///     host only reads them.
    /// </summary>
    private readonly SteamUiBackends _backends;

    private readonly SteamUiBridgeHost _bridge;

    // Serializes the start of disposal; every caller gets the one disposal task.
    private readonly Lock _disposeGate = new();

    /// <summary>Every surface this session declares, and the adapters behind them.</summary>
    private readonly SteamUiModuleCatalog _modules;

    private readonly Timer _nativeStateRefresh;
    private readonly SteamOverlayActivationPatch _overlayActivation = new();
    private readonly SteamUiPatchManager _patches;
    private readonly SteamUiModuleRuntime _runtime;
    private readonly CancellationTokenSource _shutdown = new();

    // Every switch write takes this, so the stored switches, the derivation and the patch switches
    // move as one step and the last writer always leaves the patches matching its switches. Held
    // only around synchronous switch setters, never across an await.
    private readonly Lock _switchGate = new();

    private readonly ISteamUiTransport _transport;
    private Task? _disposal;

    // Set by CloseAdmission at shutdown start: nothing is applied, queued or answered after it.
    // Retraction still runs, until _retired marks the patch registry as gone.
    private volatile bool _disposed;

    // Derived from the switches and the active glyph profile: whether the glyph stylesheet is on.
    private volatile bool _glyphDeliveryEnabled;

    private volatile bool _retired;

    // The switches the session last applied. Replaced whole under _switchGate; readers take one
    // reference and read a consistent set.
    private volatile SteamUiSurfaceSwitches _switches = SteamUiSurfaceSwitches.Off;

    // Whether WSGM's settings page can be drawn: its route and its renderer both verified. The menu
    // row is published only while it can, so a Steam update that breaks the page takes the row with
    // it rather than leaving one that opens onto nothing.
    private volatile bool _wsgmSettingsReady;

    /// <summary>Creates the host and its surface services.</summary>
    /// <param name="transport">The one process-long Steam UI transport.</param>
    /// <param name="toggleQuickAccess">Opens or closes WSGM's overlay.</param>
    /// <param name="backends">
    ///     The session-owned services behind the surfaces. The session builds and disposes them; this
    ///     host only reads them, and a surface whose backend is null is not declared.
    /// </param>
    /// <remarks>
    ///     The throwable steps (the asset, the module set, the patch registry) run before anything is
    ///     subscribed, so a host that fails to build leaves no handler behind on the session's services.
    /// </remarks>
    internal SteamUiSessionHost(
        ISteamUiTransport transport,
        Func<CancellationToken, Task<bool>> toggleQuickAccess,
        SteamUiBackends backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(toggleQuickAccess);
        // WSGM's composed asset, loaded first: it is the step most likely to throw.
        var asset = SteamUiAssetCatalog.LoadNativeQamBootstrap();
        _backends = backends;
        _modules = new SteamUiModuleCatalog(
            backends,
            _switchGate,
            () => _switches,
            () => _disposed,
            () => _wsgmSettingsReady,
            toggleQuickAccess,
            QueueStatePublication,
            _overlayActivation.Id);
        try
        {
            var modules = _modules.ComposeInitial();
            // The module-derived vocabulary, named here rather than reached for from inside the bridge.
            _bridge = new SteamUiBridgeHost(_transport, asset, modules.AllowedCommands);
            _patches = new SteamUiPatchManager(_transport);
            _patches.Register(new SteamUiBridgePatch(_bridge));
            _patches.Register(_overlayActivation);
            modules.RegisterPatches(_patches);
            lock (_switchGate)
            {
                ApplySwitchStates();
            }

            _patches.SetGlobalEnabled(false);
            // Traffic in both directions is the runtime's; which patches are applied when stays here,
            // because that is this application's policy and not a general rule.
            // Both directions follow the host surfaces, which every other surface implies: the library
            // badge reports the Home layout back and the download sort the queue positions Steam
            // refused, with or without native Quick Access.
            _runtime = new SteamUiModuleRuntime(
                _bridge,
                modules,
                _patches,
                () => _switches.HostSurfaces,
                () => _switches.HostSurfaces);
        }
        catch
        {
            // The network service polls from construction; nothing else here holds a handler yet.
            if (_modules.Network is { } network)
            {
                Log.Observe(network.DisposeAsync().AsTask(), "Steam UI network service disposal");
            }

            throw;
        }

        Subscribe();
        // Audio formats and Windows power policy can change without a manager event. Refresh the
        // existing shared projections, including QAM, while native Settings integration is enabled.
        _nativeStateRefresh = new Timer(static state =>
        {
            var host = (SteamUiSessionHost)state!;
            if (!host._disposed && host._switches.HostSurfaces)
            {
                host.QueueStatePublication();
            }
        }, this, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <inheritdoc />
    /// <remarks>Concurrent and repeated calls share one disposal.</remarks>
    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    /// <summary>Hears every change source that moves a published state. The constructor's last step.</summary>
    private void Subscribe()
    {
        _backends.Screensavers?.Changed += QueueStatePublication;
        _runtime.ModuleFailed += OnModuleFailed;
        _patches.Synchronized += OnPatchesSynchronized;
        _transport.GenerationChanged += OnGenerationChanged;
        _backends.Brightness.Changed += QueueStatePublication;
        _modules.PanelFolds.Changed += QueueStatePublication;
        LibraryBadges.Changed += OnLibraryBadgesChanged;
        _backends.DisplayTimeouts?.Changed += QueueStatePublication;
        // One subscription for every device row: the power sliders, the device controls and AutoTDP
        // all project the same capability views and coordinator configuration.
        _backends.DeviceCoordinator?.Capabilities.Changed += OnCapabilitiesChanged;
        _backends.DeviceCoordinator?.ConfigurationChanged += QueueStatePublication;
        _modules.AutoTdp.StateChanged += QueueStatePublication;
        _backends.Performance.StateChanged += OnPerformanceStateChanged;
        _modules.ControllerTarget.StateChanged += QueueStatePublication;
        // A profile change moves the game-override markers even when no device value changed.
        _backends.Profiles.Changed += OnProfilesChanged;
        if (_backends.PluginSteamUi is { } pluginSteamUi)
        {
            pluginSteamUi.Changed += QueueStatePublication;
            pluginSteamUi.ModulesChanged += OnPluginModulesChanged;
            // A plugin that turned ready between the constructor's read and this subscription.
            if (_modules.PluginModulesOutdated)
            {
                OnPluginModulesChanged();
            }
        }

        // Both host pages answer a command immediately and finish the work in the background, so
        // without these the page only ever sees the first loading snapshot: the scan or the search
        // completes, raises this, and nobody is listening. The page spins forever.
        _backends.Artwork?.Changed += QueueStatePublication;
        _backends.LibraryImport?.Changed += QueueStatePublication;
        _backends.Emulators?.Changed += QueueStatePublication;
        _backends.LaunchActions?.Changed += QueueStatePublication;
        _backends.LibraryManager?.Changed += QueueStatePublication;
        _backends.Notes?.Changed += QueueStatePublication;
        _backends.ChordMirror?.Changed += QueueStatePublication;
        _backends.WsgmSettings?.Changed += QueueStatePublication;
        _backends.Graphics?.Changed += QueueStatePublication;
        // Each page session republishes its owner's changes along with its own.
        _modules.ThemesPage?.Changed += OnThemesChanged;
        _modules.AnimationsPage?.Changed += QueueStatePublication;
        _backends.Sounds?.Changed += QueueStatePublication;
        _backends.PowerMenu?.Changed += QueueStatePublication;
        _modules.Audio?.StateChanged += QueueStatePublication;
        _modules.AudioFormat?.StateChanged += QueueStatePublication;
    }

    /// <summary>Detaches everything <see cref="Subscribe" /> attached.</summary>
    private void Unsubscribe()
    {
        _backends.Screensavers?.Changed -= QueueStatePublication;
        _runtime.ModuleFailed -= OnModuleFailed;
        _patches.Synchronized -= OnPatchesSynchronized;
        _transport.GenerationChanged -= OnGenerationChanged;
        _backends.Brightness.Changed -= QueueStatePublication;
        _modules.PanelFolds.Changed -= QueueStatePublication;
        LibraryBadges.Changed -= OnLibraryBadgesChanged;
        _backends.DisplayTimeouts?.Changed -= QueueStatePublication;
        _backends.DeviceCoordinator?.Capabilities.Changed -= OnCapabilitiesChanged;
        _backends.DeviceCoordinator?.ConfigurationChanged -= QueueStatePublication;
        _modules.AutoTdp.StateChanged -= QueueStatePublication;
        _backends.Performance.StateChanged -= OnPerformanceStateChanged;
        _modules.ControllerTarget.StateChanged -= QueueStatePublication;
        _backends.Profiles.Changed -= OnProfilesChanged;
        _backends.PluginSteamUi?.Changed -= QueueStatePublication;
        _backends.PluginSteamUi?.ModulesChanged -= OnPluginModulesChanged;
        _backends.Artwork?.Changed -= QueueStatePublication;
        _backends.LibraryImport?.Changed -= QueueStatePublication;
        _backends.Emulators?.Changed -= QueueStatePublication;
        _backends.LaunchActions?.Changed -= QueueStatePublication;
        _backends.LibraryManager?.Changed -= QueueStatePublication;
        _backends.Notes?.Changed -= QueueStatePublication;
        _backends.ChordMirror?.Changed -= QueueStatePublication;
        _backends.WsgmSettings?.Changed -= QueueStatePublication;
        _backends.Graphics?.Changed -= QueueStatePublication;
        _modules.ThemesPage?.Changed -= OnThemesChanged;
        _modules.AnimationsPage?.Changed -= QueueStatePublication;
        _backends.Sounds?.Changed -= QueueStatePublication;
        _backends.PowerMenu?.Changed -= QueueStatePublication;
        _modules.Audio?.StateChanged -= QueueStatePublication;
        _modules.AudioFormat?.StateChanged -= QueueStatePublication;
    }

    /// <summary>Retracts and disposes the host, no longer than the shutdown deadline allows.</summary>
    /// <param name="deadline">
    ///     The session's shutdown deadline. The toolkit's runtime and patch manager stop waiting when it
    ///     fires and name what they left; the patches not yet removed are reported as not removed.
    /// </param>
    /// <returns>The one disposal every caller shares.</returns>
    internal ValueTask StopAsync(CancellationToken deadline)
    {
        lock (_disposeGate)
        {
            _disposal ??= DisposeCoreAsync(deadline);
            return new ValueTask(_disposal);
        }
    }

    /// <summary>
    ///     Stops every Steam command, publication and surface write without a CEF round trip, so Steam's
    ///     Quick Access can no longer call into owners the shutdown is about to stop.
    /// </summary>
    /// <remarks>
    ///     The session calls this at the start of shutdown, before device cleanup. It is the no-CEF prefix
    ///     of <see cref="DisableAsync" />: the runtime's publish and request gates read the switches cleared
    ///     here, so every later command is refused. The patches stay in Steam until disposal retracts them
    ///     after device cleanup. Idempotent.
    /// </remarks>
    internal void CloseAdmission()
    {
        _disposed = true;
        Log.Observe(_modules.NativeSettings.StopRumblePreviewAsync(), "Stopping Steam rumble calibration preview");
        _nativeStateRefresh.Dispose();
        ClearSwitches();
    }

    private async Task DisposeCoreAsync(CancellationToken deadline)
    {
        CloseAdmission();
        Exception? retraction = null;
        try
        {
            await RetractAsync(deadline).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Disposal still releases everything below; the session records the failure.
            retraction = ex;
        }

        _retired = true;
        if (_backends.Screensavers is not null)
        {
            await _backends.Screensavers.StopForSteamAsync().ConfigureAwait(false);
        }
        // The session's services outlive this host: only the handlers attached to them go here.
        Unsubscribe();
        if (_modules.Bluetooth is not null)
        {
            await _modules.Bluetooth.StopDiscoveryAsync().ConfigureAwait(false);
        }

        // A session that ends while Steam's network page is open would otherwise leave the radio
        // sweeping and this host subscribed to a collection it no longer publishes.
        if (_modules.Network is not null)
        {
            await _modules.Network.DisposeAsync().ConfigureAwait(false);
        }

        _modules.AudioFormat?.Dispose();
        // The runtime first: it stops answering, cancels what is in flight and drains its own
        // request tasks, so nothing is still writing to the bridge when that is disposed below.
        await _runtime.ShutdownAsync(deadline).ConfigureAwait(false);
        // The pages' browsing ends once no command can reach it; the services it reads stay the session's.
        _modules.ThemesPage?.Dispose();
        _modules.AnimationsPage?.Dispose();
        // ReSharper disable once MethodHasAsyncOverload
        _shutdown.Cancel();
        // Removes whatever the retraction left, the bridge last, within the same deadline.
        await _patches.ShutdownAsync(deadline).ConfigureAwait(false);
        await _bridge.DisposeAsync().ConfigureAwait(false);
        _modules.AutoTdp.Dispose();
        _modules.Audio?.Dispose();
        _modules.ControllerTarget.Dispose();
        _shutdown.Dispose();
        if (retraction is not null)
        {
            ExceptionDispatchInfo.Throw(retraction);
        }
    }

    /// <summary>Applies every Steam surface switch the session derived from its configuration.</summary>
    /// <param name="next">The switches to hold from now on.</param>
    /// <remarks>
    ///     The one entry point for every switch. Under <c>_switchGate</c> it stores the switches, runs
    ///     the synchronous edge effects and derives every patch switch from them, so a later call always
    ///     wins over an earlier one and over a retraction that started before it. The edges:
    ///     <list type="bullet">
    ///         <item>native Quick Access going off cancels its in-flight requests at once;</item>
    ///         <item>the screensaver rows going off forget Steam's screensaver timeouts;</item>
    ///         <item>the Wi-Fi indicator going off while Quick Access is off stops the network sweep;</item>
    ///         <item>the host surfaces moving reset the sound integration line;</item>
    ///         <item>the glyph inputs moving rebuild the glyph presentation.</item>
    ///     </list>
    ///     The host surfaces are deliberately not conditional on a plugin source existing: the pages,
    ///     the game menu and the plugin tab carry WSGM's own entries. The carousel's uninstalled-games
    ///     preference alone changing is only a publication, so the carousel re-orders without being
    ///     retracted. The screensaver rows need a session timeout owner and stay off without one.
    /// </remarks>
    internal void Apply(SteamUiSurfaceSwitches next)
    {
        ArgumentNullException.ThrowIfNull(next);
        SteamUiSurfaceSwitches previous;
        lock (_switchGate)
        {
            previous = _switches;
            if (_disposed || next == previous)
            {
                return;
            }

            _switches = next;
            if (previous.Glyphs != next.Glyphs
                || !Equals(previous.GlyphProfile, next.GlyphProfile)
                || previous.NativeGlyphArtwork != next.NativeGlyphArtwork)
            {
                _modules.GlyphDelivery.Update(next.Glyphs ? next.GlyphProfile : null, next.NativeGlyphArtwork);
            }

            ApplySwitchStates();
        }

        // The edges run once the new switches are in force, so a request arriving now is refused.
        if (previous.NativeQuickAccess && !next.NativeQuickAccess)
        {
            CancelAllInflightRequests();
        }

        if (SteamUiPatchSwitches.ScreensaverRowsWanted(previous, _backends.DisplayTimeouts)
            && !SteamUiPatchSwitches.ScreensaverRowsWanted(next, _backends.DisplayTimeouts))
        {
            _backends.DisplayTimeouts?.ForgetSteam();
            if (_backends.Screensavers is not null)
            {
                Log.Observe(_backends.Screensavers.StopForSteamAsync(), "Stopping custom screensaver after disabling its surface");
            }
        }

        if (previous.NetworkIndicator && !next.NetworkIndicator && !next.NativeQuickAccess)
        {
            _modules.Network?.PostStopScanning();
        }

        if (previous.HostSurfaces != next.HostSurfaces)
        {
            _backends.Sounds?.SetHostState(next.HostSurfaces, null);
        }

        QueueSynchronization();
        // Several published states read the switches: the network header, the carousel's
        // preference and the capability hook's mask among them.
        QueueStatePublication();
    }

    /// <summary>Sets every patch switch and the global switch from the stored switches.</summary>
    /// <remarks>
    ///     The caller holds <c>_switchGate</c>. Only turns the global switch on; turning it off is the
    ///     retraction's and the synchronization handler's. The patch manager removes the gates before
    ///     the bridge they live in, so the bridge's switch needs no ordering here.
    /// </remarks>
    private void ApplySwitchStates()
    {
        var switches = _switches;
        SteamUiPatchSwitches.SetPatchStates(_patches, switches, _backends, _modules.PluginPatchIds,
            _overlayActivation.Id);
        _glyphDeliveryEnabled = SteamUiPatchSwitches.SetGlyphDeliveryPatchStates(_patches, switches,
            _modules.GlyphDelivery);
        _patches.SetPatchEnabled(_overlayActivation.Id, switches.HostSurfaces);
        if (AnyPatchWanted(switches))
        {
            _patches.SetGlobalEnabled(true);
        }
    }

    /// <summary>Re-derives every patch switch unless admission has closed. The caller holds <c>_switchGate</c>.</summary>
    private void ApplySwitchStatesIfOpen()
    {
        if (!_disposed)
        {
            ApplySwitchStates();
        }
    }

    /// <summary>Whether any patch at all should stay applied, so the global switch stays on.</summary>
    /// <remarks>
    ///     The host surfaces carry the bridge bootstrap, the publication and every other surface;
    ///     only the glyph stylesheet is delivered without the bridge.
    /// </remarks>
    private bool AnyPatchWanted(SteamUiSurfaceSwitches switches)
    {
        return switches.HostSurfaces || _glyphDeliveryEnabled;
    }

    /// <summary>Returns the immutable patch-registry view used by diagnostics and isolated tests.</summary>
    /// <returns>The patch manager's independent snapshot list with current generations and diagnostics.</returns>
    internal IReadOnlyList<SteamUiPatchSnapshot> GetPatchSnapshots()
    {
        return _patches.GetSnapshots();
    }

    /// <summary>Retracts every surface from Steam and turns the patch registry off.</summary>
    /// <remarks>
    ///     Still runs after <see cref="CloseAdmission" />, so a master-switch or Big Picture retraction
    ///     already queued at shutdown start removes what it was meant to; only a retired host skips it.
    /// </remarks>
    /// <param name="deadline">Ends the pass; what was not removed by then stays in Steam and is reported.</param>
    /// <returns>
    ///     A task completing after the retraction pass, or immediately for a retired host; the deadline and
    ///     session shutdown cancellation can interrupt the pass.
    /// </returns>
    internal async Task DisableAsync(CancellationToken deadline = default)
    {
        if (_retired)
        {
            return;
        }

        ClearSwitches();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, deadline);
        await RetractAsync(cancellation.Token).ConfigureAwait(false);
    }

    /// <summary>Asks the patch manager whether the bridge still holds in Steam's page; one evaluation.</summary>
    /// <remarks>The session calls this periodically while its transport is open, and on unlock and display changes.</remarks>
    internal void CheckHealth()
    {
        if (_disposed || _retired)
        {
            return;
        }

        _patches.QueueHealthCheck();
    }

    /// <summary>Turns every feature switch off and cancels in-flight requests, without touching CEF.</summary>
    private void ClearSwitches()
    {
        Log.Observe(_modules.NativeSettings.StopRumblePreviewAsync(), "Stopping rumble preview when Steam integration stops");
        lock (_switchGate)
        {
            _switches = SteamUiSurfaceSwitches.Off;
            // ReSharper disable once MethodHasAsyncOverload
            _patches.SetPatchEnabled(_overlayActivation.Id, false);
        }

        CancelAllInflightRequests();
    }

    /// <summary>Takes the patches out of Steam in one pass.</summary>
    /// <param name="cancellationToken">Ends the wait for the pass.</param>
    /// <remarks>
    ///     The patch manager removes the gates before the bridge they live in on every pass. The patch
    ///     switches are derived from the stored switches under <c>_switchGate</c>, so an
    ///     <see cref="Apply" /> that lands during the retraction wins rather than being overwritten with
    ///     off.
    /// </remarks>
    private async Task RetractAsync(CancellationToken cancellationToken)
    {
        if (_modules.Network is not null)
        {
            await _modules.Network.StopScanningAsync().ConfigureAwait(false);
        }

        lock (_switchGate)
        {
            ApplySwitchStates();
            if (!AnyPatchWanted(_switches))
            {
                _modules.GlyphDelivery.Update(null);
                // ReSharper disable once MethodHasAsyncOverload
                _patches.SetGlobalEnabled(false);
            }
        }

        await _patches.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        if (snapshot.Role == SteamUiTargetRole.SharedJsContext)
        {
            if (snapshot.Health == SteamUiTransportHealth.Ready)
            {
                _backends.Emulators?.RequestShortcutSync();
                _backends.LibraryImport?.NotifyManagedEntriesChanged();
            }

            Log.Observe(_modules.NativeSettings.StopRumblePreviewAsync(), "Stopping rumble preview after Steam reload");
            if (_backends.Sounds is { } sounds)
            {
                sounds.SetHostState(_switches.HostSurfaces, null);
                _ = sounds.RefreshAsync(CancellationToken.None);
            }

            // A semantic operation is authorized against one execution-context/document pair.
            // Letting it continue after either generation moved could apply a result for a page
            // that can no longer receive its response, so replacement is cancellation just like
            // an explicit bridge cancel.
            CancelAllInflightRequests();
            // A new document is a new client until its screensaver surface reports again.
            _backends.DisplayTimeouts?.ForgetSteam();
            if (_backends.Screensavers is not null)
            {
                Log.Observe(_backends.Screensavers.StopForSteamAsync(), "Stopping custom screensaver after Steam reload");
            }
        }

        // The patch manager marks patches for every changed target role, so every role change must
        // queue synchronization.
        // Every surface switch belongs here: a Steam restart replaces the SharedJSContext
        // generation, and a surface that is the only thing on would otherwise never be reapplied.
        var switches = _switches;
        if (switches.HostSurfaces
            || switches.Glyphs
            || _glyphDeliveryEnabled)
        {
            QueueSynchronization();
        }
    }

    /// <summary>Asks the patch manager for a pass; it coalesces requests into one queued pass.</summary>
    private void QueueSynchronization()
    {
        if (_disposed)
        {
            return;
        }

        _patches.QueueSynchronization();
    }

    /// <summary>Reconciles what reads patch states after each of the manager's queued passes.</summary>
    /// <remarks>
    ///     Raised by the manager on a thread of its own, after the pass released its scheduler. Turning
    ///     the global switch off queues one more pass only when the switch moves, so this settles.
    /// </remarks>
    private void OnPatchesSynchronized(object? sender, EventArgs e)
    {
        if (_retired)
        {
            return;
        }

        _backends.PluginSteamUi?.CheckPatches(_patches.GetSnapshots());

        if (_backends.Sounds is { } sounds)
        {
            var soundPatch = _patches.GetSnapshots()
                .FirstOrDefault(patch => patch.Id == SteamSoundOverrideSurface.PatchId);
            sounds.SetHostState(_switches.HostSurfaces, soundPatch);
        }

        ReconcileScreensaverReport();
        ReconcileWsgmSettingsMenu();
        // The host surfaces keep the bootstrap up, with or without native Quick Access, so the
        // library badge, the Home carousel and the screensaver rows are never retracted after a pass.
        if (_switches.HostSurfaces)
        {
            QueueStatePublication();
            return;
        }

        // Re-derived under the gate, so an Apply that landed after this pass read the
        // switches wins rather than being overwritten with off.
        lock (_switchGate)
        {
            ApplySwitchStates();
            if (!AnyPatchWanted(_switches))
            {
                // ReSharper disable once MethodHasAsyncOverload
                _patches.SetGlobalEnabled(false);
            }
        }
    }

    /// <summary>Forgets Steam's screensaver timeouts whenever the surface that reports them does not hold.</summary>
    /// <remarks>
    ///     The bound they set belongs to the client that reported it. Switching to a Steam build without
    ///     the screensaver, or turning the rows off, otherwise leaves the overlay refusing short display
    ///     timeouts for a screensaver that no longer exists. A holding surface reports again on install.
    /// </remarks>
    private void ReconcileScreensaverReport()
    {
        if (_backends.DisplayTimeouts is null)
        {
            return;
        }

        var surface = _patches.GetSnapshots().FirstOrDefault(patch => patch.Id == SteamScreensaverSurface.PatchId);

        if (!ScreensaverReportHolds(surface))
        {
            _backends.DisplayTimeouts.ForgetSteam();
            if (_backends.Screensavers is not null)
            {
                Log.Observe(_backends.Screensavers.StopForSteamAsync(), "Stopping custom screensaver after its Steam surface retired");
            }
        }
    }

    /// <summary>Publishes WSGM's menu row, or takes it down, as its page becomes drawable or stops being.</summary>
    private void ReconcileWsgmSettingsMenu()
    {
        if (_backends.WsgmSettings is null)
        {
            return;
        }

        var snapshots = _patches.GetSnapshots();
        var ready = WsgmSettingsPageReady(snapshots);
        if (ready != _wsgmSettingsReady)
        {
            _wsgmSettingsReady = ready;
            QueueStatePublication();
        }
    }

    /// <summary>Whether WSGM's settings page can be drawn: its route and its renderer both verified.</summary>
    /// <param name="snapshots">Every patch's state.</param>
    /// <returns>True only when both patches are enabled and verified.</returns>
    internal static bool WsgmSettingsPageReady(IReadOnlyList<SteamUiPatchSnapshot> snapshots)
    {
        return new[] { SteamPageSurface.PatchId, SteamWsgmSettingsSurface.PatchId }.All(id =>
            snapshots.Any(snapshot => snapshot is { Enabled: true, State: SteamUiPatchState.Verified }
                                      && snapshot.Id == id));
    }

    /// <summary>Whether a Screensaver settings patch in this state still vouches for Steam's report.</summary>
    /// <param name="surface">The patch's snapshot, or null when it is not registered.</param>
    /// <returns>True while it is enabled and applying, applied or verified.</returns>
    internal static bool ScreensaverReportHolds(SteamUiPatchSnapshot? surface)
    {
        return surface is
        {
            Enabled: true,
            State: SteamUiPatchState.Applying or SteamUiPatchState.Applied or SteamUiPatchState.Verified
        };
    }

    private void OnLibraryBadgesChanged()
    {
        lock (_switchGate)
        {
            ApplySwitchStatesIfOpen();
        }

        QueueStatePublication();
    }

    private void OnCapabilitiesChanged(IReadOnlyList<DeviceCapabilityView> views)
    {
        QueueStatePublication();
    }

    private void OnPerformanceStateChanged(PerformanceState state)
    {
        QueueStatePublication();
    }

    private void OnProfilesChanged(ProfileSnapshot snapshot, ProfileChangeKind kind)
    {
        QueueStatePublication();
    }

    // The cascade follows the themes' own switch, which changes without any of the host's feature
    // switches: off, the patch is retracted and every owned node leaves every window; on, it is
    // installed. The patch switches are derived as everywhere else, so this cannot overwrite a newer
    // switch; SetPatchEnabled synchronizes only when a switch actually moves.
    private void OnThemesChanged()
    {
        lock (_switchGate)
        {
            ApplySwitchStatesIfOpen();
        }

        QueueStatePublication();
    }

    /// <summary>Registers a plugin's modules when it becomes ready and removes them when it stops.</summary>
    private void OnPluginModulesChanged()
    {
        if (!_disposed)
        {
            Log.Observe(
                _modules.ReplacePluginModulesAsync(_runtime, ApplySwitchStatesIfOpen, QueueSynchronization, _shutdown),
                "Steam UI plugin module registration");
        }
    }

    // The runtime quarantines the module and faults its patches in the manager, which keeps them off
    // whatever the switches here say. This side only records it.
    private void OnModuleFailed(object? sender, SteamUiModuleFailure failure)
    {
        _backends.PluginSteamUi?.FailModule(failure.Module.Id, $"{failure.Operation}: {failure.Error}");
        Log.Warn($"Steam UI module {failure.Module.Id} was disabled after {failure.Operation}: {failure.Error}");
    }

    /// <summary>Queues one publication round of every Steam surface's state, unless the host is closed.</summary>
    /// <remarks>The session calls this after a display topology notification as well.</remarks>
    internal void QueueStatePublication()
    {
        if (!_disposed)
        {
            _runtime.QueuePublication();
        }
    }

    private void CancelAllInflightRequests()
    {
        _runtime.CancelAllInflight();
    }
}
