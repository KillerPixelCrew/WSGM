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
    private readonly IntelGraphicsMemoryTransport _transport;

    public SharedMemoryControl(IntelGraphicsMemoryTransport transport, string instance, Placement placement)
        : base(Descriptors.Range(
            "graphics.shared-memory",
            instance,
            "Shared GPU memory",
            IntelGraphicsMemoryTransport.MinimumPercent,
            IntelGraphicsMemoryTransport.MaximumPercent,
            1,
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
    public override ControlRead Read()
    {
        // The stored percentage, not the size the driver currently reports. Those two disagree
        // between a write and the next restart, and the row has to show what was asked for.
        return _transport.Read() is { } state
            ? ControlRead.Of(CapabilityValue.Integer(state.Percent))
            : ControlRead.Failed(IgclResult.DataNotFound);
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        return _transport.TryWrite(value.IntegerValue ?? 0) is null
            ? ControlWrite.Refuse("The driver's memory manager key could not be written; WSGM needs elevation.")
            : ControlWrite.Applied;
    }
}

/// <summary>Retro scaling: integer or nearest-neighbour upscaling for the whole adapter.</summary>
internal sealed unsafe class RetroScalingControl : IntelControl
{
    private const uint Integer = 1 << 0;
    private const uint NearestNeighbour = 1 << 1;
    private readonly IgclAdapter _adapter;
    private readonly IgclSession _session;

    private RetroScalingControl(IgclSession session, IgclAdapter adapter, CapabilityDescriptor descriptor)
        : base(descriptor)
    {
        _session = session;
        _adapter = adapter;
    }

    /// <summary>Builds the control when the adapter offers any retro scaling.</summary>
    /// <param name="session">The IGCL session.</param>
    /// <param name="adapter">The adapter.</param>
    /// <param name="instance">The adapter's instance id.</param>
    /// <param name="placement">Where it sits.</param>
    /// <returns>The control, or null when the adapter offers none.</returns>
    public static RetroScalingControl? TryCreate(
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
        caps.Size = (uint)sizeof(CtlRetroScalingCaps);
        if (session.Observe(api.GetRetroScalingCaps(adapter.Handle, &caps)) != IgclResult.Success
            || (caps.SupportedRetroScaling & (Integer | NearestNeighbour)) == 0)
        {
            return null;
        }

        List<(string, string)> choices = [("off", "Off")];
        if ((caps.SupportedRetroScaling & Integer) != 0)
        {
            choices.Add(("integer", "Integer scaling"));
        }

        if ((caps.SupportedRetroScaling & NearestNeighbour) != 0)
        {
            choices.Add(("nearest-neighbour", "Nearest neighbour"));
        }

        return new RetroScalingControl(
            session,
            adapter,
            Descriptors.Choice("graphics.retro-scaling", instance, "Retro scaling", choices, placement));
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        CtlRetroScalingSettings settings = default;
        settings.Size = (uint)sizeof(CtlRetroScalingSettings);
        settings.Get = 1;
        var result = _session.Observe(_session.Api.GetSetRetroScaling(_adapter.Handle, &settings));
        if (result != IgclResult.Success)
        {
            return ControlRead.Failed(result);
        }

        var choice = settings.Enable == 0
            ? "off"
            : (settings.RetroScalingType & NearestNeighbour) != 0
                ? "nearest-neighbour"
                : "integer";
        return ControlRead.Of(CapabilityValue.Choice(choice));
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        CtlRetroScalingSettings settings = default;
        settings.Size = (uint)sizeof(CtlRetroScalingSettings);
        settings.Get = 0;
        settings.Enable = value.ChoiceValue == "off" ? (byte)0 : (byte)1;
        settings.RetroScalingType = value.ChoiceValue switch
        {
            "nearest-neighbour" => NearestNeighbour,
            "integer" => Integer,
            _ => 0
        };
        var result = _session.Observe(_session.Api.GetSetRetroScaling(_adapter.Handle, &settings));
        return ControlWrite.From(result, "retro scaling");
    }
}
