using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Graphics;

/// <summary>The shared GPU memory override, the one value this package writes to the registry.</summary>
/// <remarks>
///     Global only and effective after a restart: the driver reads it when it sets up its memory
///     manager, so neither a game switch nor an immediate effect is possible.
/// </remarks>
internal sealed class SharedMemoryControl : IntelControl
{
    private static readonly ControlFailure Unreadable =
        new(FailureKind.Registry, Detail: "the shared-memory split could not be read");

    private readonly IntelGraphicsMemoryTransport _transport;

    public SharedMemoryControl(IntelGraphicsMemoryTransport transport, string instance, Placement placement)
        : base(Descriptors.Range(
            "graphics.shared-memory",
            instance,
            "Shared GPU memory",
            IntegerRange.Linear(IntelGraphicsMemoryTransport.MinimumPercent,
                IntelGraphicsMemoryTransport.MaximumPercent),
            CapabilityUnit.Percent,
            placement with
            {
                Scope = CapabilityProfileScope.GlobalOnly,
                Timing = CapabilityApplyTiming.SystemRestart
            }))
    {
        _transport = transport;
    }

    /// <inheritdoc />
    public override ControlWrite ProbeSupport()
    {
        return _transport.ProbeSupport()
            ? new ControlWrite(WriteStatus.Applied,
                Detail: "registry write access verified; existing override round-tripped, absent override retained")
            : ControlWrite.Refuse("The shared-memory override is not writable.", FailureKind.Registry);
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        // The stored percentage, not the size the driver currently reports. Those two disagree
        // between a write and the next restart, and the row has to show what was asked for.
        return _transport.Read() is { } percent
            ? ControlRead.Of(CapabilityValue.Integer(percent))
            : ControlRead.Failed(Unreadable);
    }

    /// <inheritdoc />
    protected override ControlWrite WriteValidated(CapabilityValue value)
    {
        return _transport.TryWrite(value.IntegerValue!.Value)
            ? ControlWrite.Applied
            : ControlWrite.Refuse("The driver's memory manager key could not be written; WSGM needs elevation.",
                FailureKind.Registry);
    }
}

/// <summary>Retro scaling: integer or nearest-neighbour upscaling for the whole adapter.</summary>
internal static unsafe class RetroScalingControl
{
    private const uint Integer = 1 << 0;
    private const uint NearestNeighbour = 1 << 1;

    private static readonly EnumMember Off = new(0, "off", "Off");

    private static readonly EnumMember[] Types =
    [
        new(Integer, "integer", "Integer scaling"),
        new(NearestNeighbour, "nearest-neighbour", "Nearest neighbour")
    ];

    /// <summary>Builds the control when the adapter offers any retro scaling.</summary>
    /// <param name="session">The IGCL session.</param>
    /// <param name="adapter">The adapter.</param>
    /// <param name="instance">The adapter's instance id.</param>
    /// <param name="placement">Where it sits.</param>
    /// <returns>The control, or null when the adapter offers none.</returns>
    public static IntelControl? TryCreate(
        IgclSession session,
        IgclAdapter adapter,
        string instance,
        Placement placement)
    {
        var api = session.Api;
        if (api.GetRetroScalingCaps is null || api.GetSetRetroScaling is null)
        {
            return null;
        }

        CtlRetroScalingCaps caps = default;
        if (session.Call(api.GetRetroScalingCaps, adapter.Handle, ref caps) != IgclResult.Success)
        {
            return null;
        }

        var supported = EnumMembers.Supported(Types, caps.SupportedRetroScaling, EnumMaskKind.Flag);
        if (supported.Count == 0)
        {
            return null;
        }

        IReadOnlyList<EnumMember> choices = [Off, .. supported];
        CtlRetroScalingSettings get = default;
        get.Get = 1;
        return new FieldControl<CtlRetroScalingSettings>(
            new IgclSource<CtlRetroScalingSettings>(session, adapter.Handle, api.GetSetRetroScaling,
                api.GetSetRetroScaling, get, static current =>
                {
                    current.Get = 0;
                    return current;
                }),
            Descriptors.Choice("graphics.retro-scaling", instance, "Retro scaling", choices, placement),
            static (control, settings) => ControlRead.Of(control.MemberOf(settings.Enable == 0
                ? 0
                : (settings.RetroScalingType & NearestNeighbour) != 0
                    ? NearestNeighbour
                    : Integer)),
            static (control, _, _, value) =>
            {
                var type = control.ValueOf(value);
                CtlRetroScalingSettings set = default;
                set.Enable = type == 0 ? (byte)0 : (byte)1;
                set.RetroScalingType = type;
                return set;
            },
            false,
            choices);
    }
}
