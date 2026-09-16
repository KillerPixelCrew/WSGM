using System.Diagnostics;
using System.Globalization;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.DeviceLab.Tests.Fakes;

public class OwnerReservationLifetimePlugin : IDevicePlugin
{
    public const string Id = "wsgm.device.synthetic.owner-reservation-lifetime";
    public const string DisposalMarker = "plugin-disposed.marker";

    public virtual string PackageId => Id;

    public virtual ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginDetectionResult
        {
            Matched = false
        });

    public ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken) => throw new InvalidOperationException(
            "A mismatched plugin must never start.");

    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask SuspendAsync(
        PluginQuiesceContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<PluginControllerRelease> ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public virtual ValueTask DisposeAsync()
    {
        var packageDirectory = Path.GetDirectoryName(typeof(OwnerReservationLifetimePlugin).Assembly.Location)!;
        File.WriteAllText(Path.Combine(packageDirectory, DisposalMarker), "disposed");
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

public sealed class HangingDetectionPlugin : OwnerReservationLifetimePlugin
{
    public new const string Id = "wsgm.device.synthetic.hanging-detection";
    public const string DescendantMarker = "hanging-descendant.pid";

    public override string PackageId => Id;

    public override ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping -t 127.0.0.1 > nul");
        var descendant = Process.Start(startInfo)
                         ?? throw new InvalidOperationException("Synthetic descendant did not start.");
        File.WriteAllText(
            Path.Combine(
                Path.GetDirectoryName(typeof(HangingDetectionPlugin).Assembly.Location)!,
                DescendantMarker),
            descendant.Id.ToString(CultureInfo.InvariantCulture));
        descendant.Dispose();
        return new ValueTask<PluginDetectionResult>(
            new TaskCompletionSource<PluginDetectionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task);
    }
}

public sealed class ThrowingDisposePlugin : OwnerReservationLifetimePlugin
{
    public new const string Id = "wsgm.device.synthetic.throwing-dispose";

    public override string PackageId => Id;

    public override ValueTask DisposeAsync() =>
        ValueTask.FromException(new InvalidOperationException("plugin disposal failed"));
}

public sealed class ThrowingConstructorPlugin : OwnerReservationLifetimePlugin
{
    public new const string Id = "wsgm.device.synthetic.throwing-constructor";
    public const string ConstructorMarker = "plugin-constructor-entered.marker";

    public ThrowingConstructorPlugin()
    {
        var packageDirectory = Path.GetDirectoryName(
            typeof(ThrowingConstructorPlugin).Assembly.Location)!;
        File.WriteAllText(Path.Combine(packageDirectory, ConstructorMarker), "entered");
        throw new InvalidOperationException("plugin constructor failed");
    }

    public override string PackageId => Id;
}

public sealed class ThrowingPackageIdPlugin : OwnerReservationLifetimePlugin
{
    public new const string Id = "wsgm.device.synthetic.throwing-package-id";
    public new const string DisposalMarker = "throwing-package-id-disposed.marker";

    public override string PackageId => throw new InvalidOperationException("package ID failed");

    public override ValueTask DisposeAsync()
    {
        var packageDirectory = Path.GetDirectoryName(
            typeof(ThrowingPackageIdPlugin).Assembly.Location)!;
        File.WriteAllText(Path.Combine(packageDirectory, DisposalMarker), "attempted");
        return ValueTask.FromException(new InvalidOperationException("plugin disposal failed"));
    }
}

public sealed class ThrowingPackageIdCleanDisposePlugin : OwnerReservationLifetimePlugin
{
    public new const string Id = "wsgm.device.synthetic.throwing-package-id-clean-dispose";

    public override string PackageId => throw new InvalidOperationException("package ID failed");
}

public class UnverifiedStopPlugin : IDevicePlugin
{
    public const string Id = "wsgm.device.synthetic.unverified-stop";

    public virtual string PackageId => Id;

    public ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginDetectionResult
        {
            Matched = true,
            DeviceDefinitionId = "synthetic-device"
        });

    public ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginStartResult
        {
            State = PluginOperationalState.Active
        });

    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask SuspendAsync(
        PluginQuiesceContext context,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginStartResult
        {
            State = PluginOperationalState.Active
        });

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginDiagnostics());

    public ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<PluginControllerRelease> ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginControllerRelease
        {
            Step = ControllerHandoffStep.TopologyVerified,
            Result = ControllerHandoffResult.ReleasedVerified
        });

    public ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public virtual ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginStopResult
        {
            Status = PluginStopStatus.Unverified,
            Reason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "synthetic restoration was unverified")
        });

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

public sealed class FailedStopPlugin : UnverifiedStopPlugin
{
    public new const string Id = "wsgm.device.synthetic.failed-stop";

    public override string PackageId => Id;

    public override ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PluginStopResult
        {
            Status = PluginStopStatus.Failed,
            Reason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "synthetic restoration failed")
        });
}

public sealed class ThrowingStopPlugin : UnverifiedStopPlugin
{
    public new const string Id = "wsgm.device.synthetic.throwing-stop";

    public override string PackageId => Id;

    public override ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken) => ValueTask.FromException<PluginStopResult>(
            new InvalidOperationException("synthetic Stop threw"));
}
