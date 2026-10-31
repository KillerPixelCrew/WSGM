using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Builders;

internal sealed class DevicePowerPresetRig
{
    internal static readonly DevicePowerPreset Battery = new("battery", "Battery", 8, 9, DevicePowerMode.BetterBattery);
    internal static readonly DevicePowerPreset Balanced = new("balanced", "Balanced", 17, 18, DevicePowerMode.Balanced);

    internal static readonly DevicePowerPreset Extreme = new("extreme", "Extreme", 30, 31,
        DevicePowerMode.BestPerformance);

    private static readonly string[] ScenarioValues = ["eco", "green", "sport", "comfort"];
    internal readonly FakePowerModeApi Api = new();
    internal readonly List<(string Id, int Watts)> Calls = [];
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly List<bool> Persistence = [];
    internal Action<string>? AfterDeviceWrite;
    internal int FailAt;
    internal string? LastScenario;
    internal bool? OnAc = true;
    internal Action<int>? OnWriteEntered;

    internal DeviceCapabilityView[] Views =
        [View(CapabilityRole.PowerSustainedLimit, 17), View(CapabilityRole.PowerSlowLimit, 18)];

    internal TaskCompletionSource? WaitForWrite;

    internal void AddFast()
    {
        Views[0] = Views[0] with
        {
            Descriptor = Views[0].Descriptor with
            {
                PowerPresets = [Battery with { FastWatts = 12 }, Balanced with { FastWatts = 25 },
                    Extreme with { FastWatts = 35 }]
            }
        };
        Views = [.. Views, View(CapabilityRole.PowerFastLimit, 20)];
    }

    internal DevicePowerPresets Create(Func<bool>? automaticPowerOwner = null)
    {
        return new DevicePowerPresets(() => Views, async (id, value, persist, token) =>
            {
                var watts = value.IntegerValue ?? 0;
                Calls.Add((id, watts));
                Persistence.Add(persist);
                OnWriteEntered?.Invoke(Calls.Count);
                Entered.TrySetResult();
                if (WaitForWrite is not null)
                {
                    await WaitForWrite.Task.WaitAsync(token);
                }

                if (FailAt == Calls.Count)
                {
                    return Completed(CommandOutcome.Indeterminate);
                }

                var index = Array.FindIndex(Views, view => view.Descriptor.CapabilityId == id);
                LastScenario = value.ChoiceValue ?? LastScenario;
                Views[index] = Views[index] with
                {
                    Projection = Views[index].Projection with
                    {
                        State = Views[index].Projection.State with
                        {
                            ObservedValue = value
                        }
                    }
                };
                AfterDeviceWrite?.Invoke(id);
                return Completed(CommandOutcome.AppliedVerified);
            }, new WindowsPowerModes(new PowerSchemes(new UnusedPowerSchemeApi()), Api), () => OnAc,
            automaticPowerOwner: automaticPowerOwner);
    }

    private static CapabilityCommandResult Completed(CommandOutcome outcome)
    {
        return new CapabilityCommandResult
        {
            CommandId = Guid.NewGuid(),
            Outcome = outcome,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    internal void AddScenarios()
    {
        Views[0] = Views[0] with
        {
            Descriptor = Views[0].Descriptor with
            {
                PowerPresets =
                [
                    Battery with { ScenarioOnAc = "eco", ScenarioOnDc = "comfort" },
                    Balanced with { ScenarioOnAc = "green", ScenarioOnDc = "comfort" },
                    Extreme with { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" }
                ]
            }
        };
        var scenario = View(CapabilityRole.ScenarioMode, 0);
        Views =
        [
            .. Views, scenario with
            {
                Descriptor = scenario.Descriptor with
                {
                    ValueKind = CapabilityValueKind.Choice,
                    Choices =
                    [
                        .. ScenarioValues.Select(value =>
                            new CapabilityChoice(value,
                                new CapabilityDisplay { Key = DisplayKey.PerformanceProfile }))
                    ]
                },
                Projection = scenario.Projection with
                {
                    State = scenario.Projection.State with
                    {
                        ObservedValue = new CapabilityValue
                            { Kind = CapabilityValueKind.Choice, ChoiceValue = "green" }
                    }
                }
            }
        ];
    }

    internal static DeviceCapabilityView View(CapabilityRole role, int watts)
    {
        return new DeviceCapabilityView(
            new CapabilityDescriptor
            {
                CapabilityId = role.ToString(),
                Role = role,
                ValueKind = CapabilityValueKind.Integer,
                Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
                Persistence = CapabilityPersistence.Volatile,
                Unit = CapabilityUnit.Watt,
                SupportsRead = true,
                SupportsWrite = true,
                Minimum = 8,
                Maximum = 37,
                Step = 1,
                PowerPresets = role == CapabilityRole.PowerSustainedLimit ? [Battery, Balanced, Extreme] : []
            },
            new CapabilityProjection
            {
                State = new CapabilityState
                {
                    CapabilityId = role.ToString(),
                    Available = true,
                    Quality = HardwareStateQuality.Verified,
                    ObservedValue = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = watts },
                    ObservedAt = DateTimeOffset.UtcNow
                }
            }, null);
    }
}
