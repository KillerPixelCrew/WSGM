using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Display;

/// <summary>The colour values WSGM last wrote per display, kept so a restart can recognise them.</summary>
/// <remarks>
///     The driver hands back the LUT and matrix it holds, not the settings behind them, and no inverse
///     is exact. So the plugin records what it wrote and, at start, accepts that record only when the
///     driver's current LUT and matrix match what the record produces. Anything else is unknown.
/// </remarks>
internal sealed class ColorStore
{
    private readonly Dictionary<string, ColorSettings> _settings;
    private readonly string? _path;

    private ColorStore(string? path, Dictionary<string, ColorSettings> settings)
    {
        _path = path;
        _settings = settings;
    }

    public static ColorStore Load(string? directory, IntelLog log)
    {
        if (directory is null)
        {
            return new ColorStore(null, new Dictionary<string, ColorSettings>(StringComparer.Ordinal));
        }

        var path = Path.Combine(directory, "color.v1.json");
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, ColorSettings>>(File.ReadAllText(path)) is { } loaded)
            {
                return new ColorStore(path, new Dictionary<string, ColorSettings>(loaded, StringComparer.Ordinal));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Warn("color", $"The colour record could not be read and is ignored: {error.Message}");
        }

        return new ColorStore(path, new Dictionary<string, ColorSettings>(StringComparer.Ordinal));
    }

    public ColorSettings? Get(string display)
    {
        return _settings.TryGetValue(display, out var settings) ? settings : null;
    }

    public void Set(string display, ColorSettings settings, IntelLog log)
    {
        _settings[display] = settings;
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_settings));
            File.Move(temporary, _path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Warn("color", $"The colour record could not be saved: {error.Message}");
        }
    }
}

/// <summary>Which colour value a control publishes.</summary>
internal enum ColorField
{
    Brightness,
    Contrast,
    Gamma,
    Hue,
    Saturation
}

/// <summary>
///     One display's colour pipe: the last 1D LUT for brightness, contrast and gamma, and the first CSC
///     matrix for hue and saturation, driven through <c>ctlPixelTransformation*</c>.
/// </summary>
/// <remarks>
///     Not offered on an HDR output. In HDR the driver reports only a non-uniformly sampled gamma LUT
///     and its 3D LUT is unsupported, so the SDR algorithms here would be wrong. Every write carries
///     <c>CTL_PIXTX_PIPE_SET_CONFIG_FLAG_PERSIST_ACROSS_POWER_EVENTS</c> so the result survives sleep.
/// </remarks>
internal sealed unsafe class ColorPipeline
{
    private const int BlockType1dLut = 1;
    private const int BlockType3x3Matrix = 3;
    private const int QueryCapability = 0;
    private const int QueryCurrent = 1;
    private const int OperationSetCustom = 2;
    private const uint FlagPersist = 1;
    private const int SamplingUniform = 0;
    private const int EncodingSt2084 = 2;
    private const int EncodingHlg = 3;
    private const uint FeatureHdr = 1 << 5;
    private const uint MaxSamples = 4096;

    private readonly CtlPixTxBlockConfig _curve;
    private readonly string _display;
    private readonly IntelLog _log;
    private readonly CtlPixTxBlockConfig? _matrix;
    private readonly IgclOutput _output;
    private readonly IgclSession _session;
    private readonly ColorStore _store;
    private long _observedAt;
    private (int Result, int? Brightness, int? Contrast, int? Gamma, int? Hue, int? Saturation) _observed;

    private ColorPipeline(
        IgclSession session,
        IgclOutput output,
        string display,
        CtlPixTxBlockConfig curve,
        CtlPixTxBlockConfig? matrix,
        ColorStore store,
        IntelLog log)
    {
        _session = session;
        _output = output;
        _display = display;
        _curve = curve;
        _matrix = matrix;
        _store = store;
        _log = log;
    }

    /// <summary>Whether hue and saturation can be offered.</summary>
    public bool HasMatrix => _matrix is not null;

    /// <summary>Queries the pipe and builds the pipeline when an SDR 1D LUT is available.</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">The output.</param>
    /// <param name="display">The display's instance id, the key of the colour record.</param>
    /// <param name="store">The colour record.</param>
    /// <param name="log">Receives the decisions.</param>
    /// <returns>The pipeline, or null when colour cannot be offered.</returns>
    public static ColorPipeline? TryCreate(
        IgclSession session,
        IgclOutput output,
        string display,
        ColorStore store,
        IntelLog log)
    {
        var api = session.Api;
        if (api.PixTxGetConfig is null || api.PixTxSetConfig is null)
        {
            return null;
        }

        if ((output.Properties.FeatureEnabledFlags & FeatureHdr) != 0)
        {
            log.Info("color", $"{display} is in HDR; colour controls are not offered.");
            return null;
        }

        CtlPixTxPipeGetConfig caps = default;
        caps.Size = (uint)sizeof(CtlPixTxPipeGetConfig);
        caps.QueryType = QueryCapability;
        var result = session.Observe(api.PixTxGetConfig(output.Handle, &caps));
        if (result != IgclResult.Success || caps.NumBlocks is 0 or > 32)
        {
            log.Info("color", $"{display} reports no colour pipe ({IgclResult.Describe(result)}).");
            return null;
        }

        if (caps.OutputFormat.EncodingType is EncodingSt2084 or EncodingHlg)
        {
            log.Info("color", $"{display} outputs an HDR encoding; colour controls are not offered.");
            return null;
        }

        var blocks = new CtlPixTxBlockConfig[caps.NumBlocks];
        fixed (CtlPixTxBlockConfig* buffer = blocks)
        {
            caps.BlockConfigs = (nint)buffer;
            result = session.Observe(api.PixTxGetConfig(output.Handle, &caps));
        }

        if (result != IgclResult.Success)
        {
            log.Info("color", $"{display} colour blocks refused ({IgclResult.Describe(result)}).");
            return null;
        }

        CtlPixTxBlockConfig? curve = null;
        CtlPixTxBlockConfig? matrix = null;
        foreach (var block in blocks)
        {
            // Intel's sample takes the last 1D LUT for desktop gamma: in HDR only that one is reported.
            if (block.BlockType == BlockType1dLut)
            {
                curve = block;
            }
            else if (block.BlockType == BlockType3x3Matrix && matrix is null)
            {
                matrix = block;
            }
        }

        if (curve is not { } lut
            || lut.Config.OneDLut.SamplingType != SamplingUniform
            || lut.Config.OneDLut.SamplesPerChannel is < 2 or > MaxSamples
            || lut.Config.OneDLut.Channels is not (1 or 3))
        {
            log.Info("color", $"{display} has no uniformly sampled 1D LUT; colour controls are not offered.");
            return null;
        }

        return new ColorPipeline(session, output, display, lut, matrix, store, log);
    }

    /// <summary>Reads one field.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The value, or null when the driver holds something WSGM cannot name.</returns>
    public ControlRead Read(ColorField field)
    {
        if (Environment.TickCount64 - _observedAt > 500)
        {
            Observe();
        }

        var value = field switch
        {
            ColorField.Brightness => _observed.Brightness,
            ColorField.Contrast => _observed.Contrast,
            ColorField.Gamma => _observed.Gamma,
            ColorField.Hue => _observed.Hue,
            _ => _observed.Saturation
        };
        return value is { } known
            ? ControlRead.Of(CapabilityValue.Integer(known))
            : ControlRead.Failed(_observed.Result);
    }

    /// <summary>Writes one field, carrying the others of its block.</summary>
    /// <param name="field">The field.</param>
    /// <param name="value">The value.</param>
    /// <returns>How the driver answered.</returns>
    /// <remarks>
    ///     When the driver holds a curve or matrix WSGM did not write, its values are unknown and have
    ///     been published as such, so the other fields of that block cannot be carried: the write starts
    ///     them from neutral and says so in the log.
    /// </remarks>
    public ControlWrite Write(ColorField field, int value)
    {
        Observe();
        var matrixBlock = field is ColorField.Hue or ColorField.Saturation;
        var foreign = matrixBlock
            ? _observed.Hue is null || _observed.Saturation is null
            : _observed.Brightness is null || _observed.Contrast is null || _observed.Gamma is null;
        if (foreign)
        {
            _log.Info(
                "color",
                $"{_display}: the driver holds a {(matrixBlock ? "colour matrix" : "tone curve")} WSGM did not "
                + $"write; writing {field.ToString().ToLowerInvariant()} replaces it and starts "
                + $"{(matrixBlock ? "hue and saturation" : "brightness, contrast and gamma")} from neutral.");
        }

        var neutral = ColorSettings.Neutral;
        var settings = new ColorSettings(
            _observed.Brightness ?? neutral.Brightness,
            _observed.Contrast ?? neutral.Contrast,
            _observed.Gamma ?? neutral.Gamma,
            _observed.Hue ?? neutral.Hue,
            _observed.Saturation ?? neutral.Saturation);
        settings = field switch
        {
            ColorField.Brightness => settings with { Brightness = value },
            ColorField.Contrast => settings with { Contrast = value },
            ColorField.Gamma => settings with { Gamma = value },
            ColorField.Hue => settings with { Hue = value },
            _ => settings with { Saturation = value }
        };

        var result = field is ColorField.Hue or ColorField.Saturation
            ? WriteMatrix(settings)
            : WriteCurve(settings);
        _observedAt = 0;
        var write = ControlWrite.From(result, $"the {field.ToString().ToLowerInvariant()} of {_display}");
        if (write.Status == WriteStatus.Applied)
        {
            _store.Set(_display, settings, _log);
        }

        return write;
    }

    private void Observe()
    {
        _observedAt = Environment.TickCount64;
        var stored = _store.Get(_display);
        var neutral = ColorSettings.Neutral;
        _observed = (IgclResult.Success, null, null, null, null, null);

        var curveResult = ReadCurve(out var samples);
        if (curveResult == IgclResult.Success)
        {
            var channel = samples.AsSpan(0, (int)_curve.Config.OneDLut.SamplesPerChannel);
            var known = ColorMath.CurveMatches(neutral, channel)
                ? neutral
                : stored is { } record && ColorMath.CurveMatches(record, channel)
                    ? record
                    : (ColorSettings?)null;
            if (known is { } curve)
            {
                _observed.Brightness = curve.Brightness;
                _observed.Contrast = curve.Contrast;
                _observed.Gamma = curve.Gamma;
                _log.Change(DeviceTraceLevel.Info, "color", $"{_display}.curve",
                    "The display's tone curve is the identity or WSGM's own.");
            }
            else
            {
                _log.Change(DeviceTraceLevel.Info, "color", $"{_display}.curve",
                    "The display holds a tone curve WSGM did not write; brightness, contrast and gamma are unknown.");
            }
        }
        else
        {
            _observed.Result = curveResult;
        }

        if (_matrix is null)
        {
            return;
        }

        var matrixResult = ReadMatrix(out var coefficients);
        if (matrixResult == IgclResult.Success)
        {
            var known = ColorMath.MatrixMatches(neutral, coefficients)
                ? neutral
                : stored is { } record && ColorMath.MatrixMatches(record, coefficients)
                    ? record
                    : (ColorSettings?)null;
            if (known is { } matrix)
            {
                _observed.Hue = matrix.Hue;
                _observed.Saturation = matrix.Saturation;
                _log.Change(DeviceTraceLevel.Info, "color", $"{_display}.matrix",
                    "The display's colour matrix is the identity or WSGM's own.");
            }
            else
            {
                _log.Change(DeviceTraceLevel.Info, "color", $"{_display}.matrix",
                    "The display holds a colour matrix WSGM did not write; hue and saturation are unknown.");
            }
        }
        else if (_observed.Result == IgclResult.Success)
        {
            _observed.Result = matrixResult;
        }
    }

    private int ReadCurve(out double[] samples)
    {
        var lut = _curve.Config.OneDLut;
        var count = (int)(lut.SamplesPerChannel * lut.Channels);
        samples = new double[count];
        var block = _curve;
        block.Size = (uint)sizeof(CtlPixTxBlockConfig);
        fixed (double* values = samples)
        {
            block.Config.OneDLut.SampleValues = (nint)values;
            block.Config.OneDLut.SamplePositions = 0;
            var result = Query(&block);
            if (result == IgclResult.DataNotFound)
            {
                // Nothing set yet is the driver's own curve, which is the neutral one.
                ColorMath.FillCurve(ColorSettings.Neutral, samples);
                return IgclResult.Success;
            }

            return result;
        }
    }

    private int ReadMatrix(out double[] coefficients)
    {
        coefficients = new double[9];
        var block = _matrix!.Value;
        block.Size = (uint)sizeof(CtlPixTxBlockConfig);
        var result = Query(&block);
        if (result == IgclResult.DataNotFound)
        {
            coefficients = [1, 0, 0, 0, 1, 0, 0, 0, 1];
            return IgclResult.Success;
        }

        if (result != IgclResult.Success)
        {
            return result;
        }

        for (var index = 0; index < 9; index++)
        {
            coefficients[index] = block.Config.Matrix.Matrix[index];
        }

        return result;
    }

    private int Query(CtlPixTxBlockConfig* block)
    {
        CtlPixTxPipeGetConfig request = default;
        request.Size = (uint)sizeof(CtlPixTxPipeGetConfig);
        request.QueryType = QueryCurrent;
        request.NumBlocks = 1;
        request.BlockConfigs = (nint)block;
        return _session.Observe(_session.Api.PixTxGetConfig(_output.Handle, &request));
    }

    private int WriteCurve(ColorSettings settings)
    {
        var lut = _curve.Config.OneDLut;
        var perChannel = (int)lut.SamplesPerChannel;
        var samples = new double[perChannel * lut.Channels];
        for (var channel = 0; channel < lut.Channels; channel++)
        {
            ColorMath.FillCurve(settings, samples.AsSpan(channel * perChannel, perChannel));
        }

        var block = _curve;
        block.Size = (uint)sizeof(CtlPixTxBlockConfig);
        fixed (double* values = samples)
        {
            block.Config.OneDLut.SampleValues = (nint)values;
            block.Config.OneDLut.SamplePositions = 0;
            block.Config.OneDLut.SamplingType = SamplingUniform;
            return Apply(&block);
        }
    }

    private int WriteMatrix(ColorSettings settings)
    {
        var block = _matrix!.Value;
        block.Size = (uint)sizeof(CtlPixTxBlockConfig);
        var coefficients = ColorMath.HueSaturationMatrix(settings);
        for (var index = 0; index < 3; index++)
        {
            block.Config.Matrix.PreOffsets[index] = 0;
            block.Config.Matrix.PostOffsets[index] = 0;
        }

        for (var index = 0; index < 9; index++)
        {
            block.Config.Matrix.Matrix[index] = coefficients[index];
        }

        return Apply(&block);
    }

    private int Apply(CtlPixTxBlockConfig* block)
    {
        CtlPixTxPipeSetConfig request = default;
        request.Size = (uint)sizeof(CtlPixTxPipeSetConfig);
        request.OperationType = OperationSetCustom;
        request.Flags = FlagPersist;
        request.NumBlocks = 1;
        request.BlockConfigs = (nint)block;
        return _session.Observe(_session.Api.PixTxSetConfig(_output.Handle, &request));
    }
}

/// <summary>One colour value of one display.</summary>
internal sealed class ColorControl : IntelControl
{
    private readonly ColorField _field;
    private readonly ColorPipeline _pipeline;

    public ColorControl(ColorPipeline pipeline, ColorField field, CapabilityDescriptor descriptor)
        : base(descriptor)
    {
        _pipeline = pipeline;
        _field = field;
    }

    /// <summary>Builds the colour controls of one display.</summary>
    /// <param name="pipeline">The pipe.</param>
    /// <param name="instance">The display's instance id.</param>
    /// <param name="placement">Where they sit.</param>
    /// <returns>The controls.</returns>
    public static IEnumerable<ColorControl> Build(ColorPipeline pipeline, string instance, Placement placement)
    {
        yield return new ColorControl(pipeline, ColorField.Brightness, Descriptors.Range("display.color-brightness",
            instance, "Brightness", ColorSettings.BrightnessMinimum, ColorSettings.BrightnessMaximum, 1,
            CapabilityUnit.None, placement));
        yield return new ColorControl(pipeline, ColorField.Contrast, Descriptors.Range("display.color-contrast",
            instance, "Contrast", ColorSettings.ContrastMinimum, ColorSettings.ContrastMaximum, 1,
            CapabilityUnit.Percent, placement with { Order = placement.Order + 1 }));
        yield return new ColorControl(pipeline, ColorField.Gamma, Descriptors.Range("display.color-gamma", instance,
            "Gamma", ColorSettings.GammaMinimum, ColorSettings.GammaMaximum, 1, CapabilityUnit.None,
            placement with { Order = placement.Order + 2 }));
        if (!pipeline.HasMatrix)
        {
            yield break;
        }

        yield return new ColorControl(pipeline, ColorField.Hue, Descriptors.Range("display.color-hue", instance, "Hue",
            ColorSettings.HueMinimum, ColorSettings.HueMaximum, 1, CapabilityUnit.None,
            placement with { Order = placement.Order + 3 }));
        yield return new ColorControl(pipeline, ColorField.Saturation, Descriptors.Range("display.color-saturation",
            instance, "Saturation", ColorSettings.SaturationMinimum, ColorSettings.SaturationMaximum, 1,
            CapabilityUnit.Percent, placement with { Order = placement.Order + 4 }));
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        return _pipeline.Read(_field);
    }

    /// <inheritdoc />
    public override ControlWrite Write(CapabilityValue value)
    {
        return _pipeline.Write(_field, value.IntegerValue ?? 0);
    }
}
