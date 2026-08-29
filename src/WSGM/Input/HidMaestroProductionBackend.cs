using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

internal sealed class HidMaestroProductionBackend : IHidBackend
{
    internal const string PinnedVersion = "1.7.0";
    internal const string PinnedCommit = "46054b862830fcec7bc98d72ccb7c4f0c0179fb1";
    internal const string PinnedArchiveSha256 =
        "A146AB8A46D2E9CE1FB2EA269FF231830607876F6F4DB7BB13CE891EF33DEECE";
    internal const string PinnedCoreSha256 =
        "BD42A99BCB260435CE25796C54A4B792F8A2CED6AB78659C0CF926011663938E";

    private bool _disposed;

    public event EventHandler<HidTargetOutput>? OutputReceived
    {
        add { }
        remove { }
    }

    public event EventHandler<long>? TargetLost
    {
        add { }
        remove { }
    }

    public Task<HidBackendHealth> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return Task.FromResult(new HidBackendHealth(
            HidBackendHealthState.Incompatible,
            DeviceFeatureAvailability.ControllerManagementDetail));
    }

    public Task<HidTargetHandle> CreateTargetAsync(
        VirtualTargetKind kind,
        CanonicalControllerSample initialNeutralState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialNeutralState);
        return Task.FromException<HidTargetHandle>(Unavailable(cancellationToken));
    }

    public Task<bool> WaitForEnumerationAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken) =>
        Task.FromException<bool>(Unavailable(cancellationToken));

    public ValueTask<bool> PublishAsync(
        HidTargetHandle target,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return new ValueTask<bool>(Task.FromException<bool>(Unavailable(cancellationToken)));
    }

    public Task NeutralizeAsync(
        HidTargetHandle target,
        CanonicalControllerSample neutralState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(neutralState);
        return Task.FromException(Unavailable(cancellationToken));
    }

    public Task RemoveTargetAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken) =>
        Task.FromException(Unavailable(cancellationToken));

    public Task<bool> WaitForRemovalAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken) =>
        Task.FromException<bool>(Unavailable(cancellationToken));

    public Task<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        IReadOnlyDictionary<string, string> diagnostics = new Dictionary<string, string>
        {
            ["health"] = HidBackendHealthState.Incompatible.ToString(),
            ["policy"] = "controller-backend-incomplete",
            ["controllerManagementApproved"] = bool.FalseString,
            ["hidMaestroVersion"] = PinnedVersion,
            ["hidMaestroCommit"] = PinnedCommit,
            ["hidMaestroArchiveSha256"] = PinnedArchiveSha256,
            ["hidMaestroCoreSha256"] = PinnedCoreSha256,
            ["detail"] = DeviceFeatureAvailability.ControllerManagementDetail,
        };
        return Task.FromResult(diagnostics);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private Exception Unavailable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return new InvalidOperationException(DeviceFeatureAvailability.ControllerManagementDetail);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
