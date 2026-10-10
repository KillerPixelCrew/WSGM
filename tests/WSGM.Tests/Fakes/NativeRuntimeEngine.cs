using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;

namespace WSGM.Tests.Fakes;

/// <summary>A deterministic native engine exercised through the real HandheldDevice owner.</summary>
internal sealed class NativeRuntimeEngine(List<string> calls, Func<string, CancellationToken, Task> hook) : NativeEngine
{
    internal const string DefinitionId = "fixture-device";
    internal const string FamilyId = "fixture";
    private EngineStartContext? _context;

    public async ValueTask<EngineStartResult> StartAsync(EngineStartContext context, CancellationToken token)
    {
        _context = context;
        await CallAsync("detect", token);
        if (context.Identity.SystemManufacturer == "passive-test")
        {
            return new EngineStartResult { State = EngineOperationalState.Passive };
        }

        await CallAsync("start", token);
        await PublishAsync(token);
        return new EngineStartResult { State = EngineOperationalState.Active };
    }

    public async ValueTask<EngineStartResult> ResumeAsync(EngineResumeContext context, CancellationToken token)
    {
        await CallAsync("resume", token);
        await PublishAsync(token);
        return new EngineStartResult { State = EngineOperationalState.Active };
    }

    public async ValueTask SuspendAsync(EngineQuiesceContext context, CancellationToken token)
    {
        await CallAsync("suspend", token);
    }

    public async ValueTask<EngineStopResult> StopAsync(EngineStopContext context, CancellationToken token)
    {
        await CallAsync("stop", token);
        return new EngineStopResult { Status = EngineStopStatus.Clean };
    }

    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CommandResults.Applied(command));
    }

    public ValueTask<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(CancellationToken token)
    {
        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseControllerAsync(EngineControllerReleaseContext context, CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask SetControllerManagementAsync(EngineControllerManagementContext context, CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CallAsync("dispose", CancellationToken.None);
        if (_context is { } context)
        {
            Directory.CreateDirectory(context.StateDirectory);
            File.WriteAllText(Path.Combine(context.StateDirectory, "disposed.txt"), "disposed");
        }
    }

    private async Task CallAsync(string name, CancellationToken token)
    {
        calls.Add(name);
        await hook(name, token);
    }

    private async ValueTask PublishAsync(CancellationToken token)
    {
        var host = _context!.Host;
        await host.PublishDescriptorsAsync(new CapabilityDescriptorSet
        {
            Descriptors =
            [
                new CapabilityDescriptor
                {
                    CapabilityId = "fixture.value", Role = CapabilityRole.GenericToggle,
                    ValueKind = CapabilityValueKind.Boolean, SupportsRead = true, SupportsWrite = true,
                    Persistence = CapabilityPersistence.Volatile,
                    Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = "Fixture" }
                }
            ]
        }, token);
        await host.PublishCapabilityStateAsync(new CapabilityState
        {
            CapabilityId = "fixture.value", Available = true, Quality = HardwareStateQuality.Observed,
            ObservedValue = CapabilityValue.Boolean(false), ObservedAt = DateTimeOffset.UtcNow
        }, token);
    }
}
