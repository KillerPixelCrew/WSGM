using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Display;

/// <summary>The colour values WSGM last wrote per display, kept so a restart can recognise them.</summary>
/// <remarks>
///     The driver hands back the LUT and matrix it holds, not the settings behind them, and no inverse
///     is exact. So the plugin records what it wrote and, at start, accepts that record only when the
///     driver's current LUT and matrix match what the record produces. Anything else is unknown. A record
///     that cannot be read is never overwritten; colour then reads as unknown and writes still apply.
/// </remarks>
internal sealed class ColorStore
{
    private const string Scope = "color";
    private const string What = "colour record";
    private readonly IntelLog _log;
    private readonly string? _path;
    private readonly Dictionary<string, ColorSettings> _settings;

    private ColorStore(string? path, Dictionary<string, ColorSettings> settings, IntelLog log)
    {
        _path = path;
        _settings = settings;
        _log = log;
    }

    public static ColorStore Load(string? directory, IntelLog log)
    {
        var path = directory is null ? null : Path.Combine(directory, "color.v1.json");
        var settings = new Dictionary<string, ColorSettings>(StringComparer.Ordinal);
        if (path is null)
        {
            return new ColorStore(path, settings, log);
        }

        try
        {
            foreach (var (display, recorded) in DriverStateFile.Read(path, new Dictionary<string, ColorSettings>()))
            {
                settings[display] = recorded;
            }
        }
        catch (DriverFailure failure)
        {
            log.Warn(Scope, $"{failure.Message} The {What} is left as it is and not written this session.");
            path = null;
        }

        return new ColorStore(path, settings, log);
    }

    public ColorSettings? Get(string display)
    {
        return _settings.TryGetValue(display, out var settings) ? settings : null;
    }

    /// <summary>Records what a write applied; a failed save is logged and costs only recognition later.</summary>
    /// <param name="display">Stable output instance identity used by the persisted color record.</param>
    /// <param name="settings">Values successfully written to that display; retained in memory even if persistence fails.</param>
    public void Set(string display, ColorSettings settings)
    {
        _settings[display] = settings;
        if (_path is null)
        {
            return;
        }

        try
        {
            DriverStateFile.Write(_path, _settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Error(Scope, $"The {What} could not be saved: {IntelLog.Describe(error)}");
        }
    }
}

/// <summary>One colour value a control publishes, and where it sits in <see cref="ColorSettings" />.</summary>
internal sealed class ColorField
{
    public static readonly ColorField Brightness = new("display.color-brightness", "Brightness",
        ColorSettings.BrightnessMinimum, ColorSettings.BrightnessMaximum, CapabilityUnit.None, false,
        static settings => settings.Brightness, static (settings, value) => settings with { Brightness = value });

    public static readonly ColorField Contrast = new("display.color-contrast", "Contrast",
        ColorSettings.ContrastMinimum, ColorSettings.ContrastMaximum, CapabilityUnit.Percent, false,
        static settings => settings.Contrast, static (settings, value) => settings with { Contrast = value });

    public static readonly ColorField Gamma = new("display.color-gamma", "Gamma",
        ColorSettings.GammaMinimum, ColorSettings.GammaMaximum, CapabilityUnit.None, false,
        static settings => settings.Gamma, static (settings, value) => settings with { Gamma = value });

    public static readonly ColorField Hue = new("display.color-hue", "Hue",
        ColorSettings.HueMinimum, ColorSettings.HueMaximum, CapabilityUnit.None, true,
        static settings => settings.Hue, static (settings, value) => settings with { Hue = value });

    public static readonly ColorField Saturation = new("display.color-saturation", "Saturation",
        ColorSettings.SaturationMinimum, ColorSettings.SaturationMaximum, CapabilityUnit.Percent, true,
        static settings => settings.Saturation, static (settings, value) => settings with { Saturation = value });

    private ColorField(
        string id,
        string label,
        int minimum,
        int maximum,
        CapabilityUnit unit,
        bool inMatrix,
        Func<ColorSettings, int> get,
        Func<ColorSettings, int, ColorSettings> set)
    {
        Id = id;
        Label = label;
        Range = IntegerRange.Linear(minimum, maximum);
        Unit = unit;
        InMatrix = inMatrix;
        Get = get;
        Set = set;
    }

    /// <summary>Every field, in row order; the matrix fields last.</summary>
    public static IReadOnlyList<ColorField> All { get; } = [Brightness, Contrast, Gamma, Hue, Saturation];

    public string Id { get; }

    public string Label { get; }

    public IntegerRange Range { get; }

    public CapabilityUnit Unit { get; }

    /// <summary>Whether the value lives in the CSC matrix (hue, saturation) rather than the 1D LUT.</summary>
    public bool InMatrix { get; }

    public Func<ColorSettings, int> Get { get; }

    public Func<ColorSettings, int, ColorSettings> Set { get; }
}

/// <summary>
///     One display's colour pipe: the last 1D LUT for brightness, contrast and gamma, and the first CSC
///     matrix for hue and saturation, driven through <c>ctlPixelTransformation*</c>.
/// </summary>
/// <remarks>
///     Not offered on an HDR output. In HDR the driver reports only a non-uniformly sampled gamma LUT
///     and its 3D LUT is unsupported, so the SDR algorithms here would be wrong. Every write carries
///     <c>CTL_PIXTX_PIPE_SET_CONFIG_FLAG_PERSIST_ACROSS_POWER_EVENTS</c> so the result survives sleep.
///     <para>
///         The pipe is read once per pass and serves all five rows. The sample buffer, the neutral curve
///         and the curve and matrix of the recorded settings are built once and reused, so a pass
///         allocates nothing.
///     </para>
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
    private const int MatrixSize = 9;

    private static readonly double[] IdentityMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1];
    private readonly double[] _coefficients = new double[MatrixSize];

    private readonly string _curveKey;
    private readonly string _display;
    private readonly IntelLog _log;
    private readonly CtlPixTxBlockConfig _lutBlock;
    private readonly CtlPixTxBlockConfig? _matrixBlock;
    private readonly string _matrixKey;
    private readonly double[] _neutralCurve;
    private readonly IgclOutput _output;
    private readonly double[] _recordedCurve;
    private readonly double[] _recordedMatrix = new double[MatrixSize];
    private readonly double[] _samples;
    private readonly IgclSession _session;
    private readonly ColorStore _store;
    private ColorSettings? _curve;
    private ColorSettings? _matrix;
    private long _observedPass;
    private ColorSettings? _recorded;
    private int _result;

    private ColorPipeline(
        IgclSession session,
        IgclOutput output,
        string display,
        CtlPixTxBlockConfig lutBlock,
        CtlPixTxBlockConfig? matrixBlock,
        ColorStore store,
        IntelLog log)
    {
        _session = session;
        _output = output;
        _display = display;
        _lutBlock = lutBlock;
        _matrixBlock = matrixBlock;
        _store = store;
        _log = log;
        _curveKey = $"{display}.curve";
        _matrixKey = $"{display}.matrix";
        var perChannel = (int)lutBlock.Config.OneDLut.SamplesPerChannel;
        _samples = new double[perChannel * lutBlock.Config.OneDLut.Channels];
        _neutralCurve = new double[perChannel];
        _recordedCurve = new double[perChannel];
        ColorMath.FillCurve(ColorSettings.Neutral, _neutralCurve);
    }

    /// <summary>Whether hue and saturation can be offered.</summary>
    public bool HasMatrix => _matrixBlock is not null;

    /// <summary>The support key of the LUT or the matrix: the plugin probes each block once.</summary>
    /// <param name="matrix">True for the matrix block; false for the one-dimensional LUT.</param>
    /// <returns>A display-qualified key shared by every control that probes the same native block.</returns>
    public string SupportKey(bool matrix)
    {
        return matrix ? _matrixKey : _curveKey;
    }

    /// <summary>Probes a block setter with its raw current LUT or matrix, without rebuilding it.</summary>
    /// <param name="matrix">Whether to probe the matrix rather than the LUT; true requires HasMatrix.</param>
    /// <param name="admission">Rechecked after the query and before the setter.</param>
    /// <remarks>A block the driver holds nothing for is its neutral default; nothing is written for it.</remarks>
    /// <returns>The exact-block read/write outcome; a missing stored block succeeds without creating an override.</returns>
    public ControlWrite ProbeSupport(bool matrix, WriteAdmission admission)
    {
        var block = matrix ? _matrixBlock!.Value : _lutBlock;
        block.Size = (uint)sizeof(CtlPixTxBlockConfig);
        int result;
        fixed (double* samples = _samples)
        {
            if (!matrix)
            {
                block.Config.OneDLut.SampleValues = (nint)samples;
                block.Config.OneDLut.SamplePositions = 0;
            }

            result = Query(&block);
            if (result == IgclResult.DataNotFound)
            {
                return new ControlWrite(WriteStatus.Applied,
                    Detail: "no stored block; the driver's neutral default is retained");
            }

            if (result == IgclResult.Success)
            {
                result = Apply(&block, admission, 0);
            }
        }

        return ControlWrite.From(result, "colour pipe support discovery");
    }

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
        caps.QueryType = QueryCapability;
        var result = session.Call(api.PixTxGetConfig, output.Handle, ref caps);
        if (result != IgclResult.Success || caps.NumBlocks == 0)
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
            result = session.Call(api.PixTxGetConfig, output.Handle, ref caps);
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
            || lut.Config.OneDLut.SamplesPerChannel < 2
            || lut.Config.OneDLut.Channels is not (1 or 3))
        {
            log.Info("color", $"{display} has no uniformly sampled 1D LUT; colour controls are not offered.");
            return null;
        }

        return new ColorPipeline(session, output, display, lut, matrix, store, log);
    }

    /// <summary>Reads one field.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The value, or unknown when the driver holds something WSGM cannot name.</returns>
    public ControlRead Read(ColorField field)
    {
        Observe(false);
        if ((field.InMatrix ? _matrix : _curve) is { } known)
        {
            return ControlRead.Of(CapabilityValue.Integer(field.Get(known)));
        }

        return _result == IgclResult.Success ? ControlRead.Of(null) : ControlRead.Driver(_result);
    }

    /// <summary>Writes one field, carrying the others of its block.</summary>
    /// <param name="field">The field.</param>
    /// <param name="value">The value.</param>
    /// <param name="admission">Rechecked after preparatory reads and before the setter.</param>
    /// <returns>How the driver answered.</returns>
    /// <remarks>
    ///     When the driver holds a curve or matrix WSGM did not write, its values are unknown and have
    ///     been published as such, so the other fields of that block cannot be carried: the write starts
    ///     them from neutral and says so in the log.
    /// </remarks>
    public ControlWrite Write(ColorField field, int value, WriteAdmission admission)
    {
        Observe(true);
        var neutral = ColorSettings.Neutral;
        if ((field.InMatrix ? _matrix : _curve) is null)
        {
            _log.Info(
                "color",
                $"{_display}: the driver holds a {(field.InMatrix ? "colour matrix" : "tone curve")} WSGM did not "
                + $"write; writing {field.Label.ToLowerInvariant()} replaces it and starts "
                + $"{(field.InMatrix ? "hue and saturation" : "brightness, contrast and gamma")} from neutral.");
        }

        var curve = _curve ?? neutral;
        var matrix = _matrix ?? neutral;
        var settings = field.Set(
            new ColorSettings(curve.Brightness, curve.Contrast, curve.Gamma, matrix.Hue, matrix.Saturation),
            value);
        var result = field.InMatrix ? WriteMatrix(settings, admission) : WriteCurve(settings, admission);
        _observedPass = 0;
        var write = ControlWrite.From(result, $"the {field.Label.ToLowerInvariant()} of {_display}");
        if (write.Status == WriteStatus.Applied)
        {
            _store.Set(_display, settings);
        }

        return write;
    }

    /// <summary>Reads the pipe once per pass and recognises what it holds.</summary>
    /// <param name="fresh">Whether to read even when this pass already did, before a write.</param>
    private void Observe(bool fresh)
    {
        if (!fresh && _observedPass == _session.Pass)
        {
            return;
        }

        _observedPass = _session.Pass;
        _result = IgclResult.Success;
        _curve = null;
        _matrix = null;
        var neutral = ColorSettings.Neutral;
        var stored = Recorded();

        var curveResult = ReadCurve();
        if (curveResult == IgclResult.Success)
        {
            var channel = _samples.AsSpan(0, _neutralCurve.Length);
            _curve = ColorMath.Matches(_neutralCurve, channel)
                ? neutral
                : stored is { } record && ColorMath.Matches(_recordedCurve, channel)
                    ? record
                    : null;
            _log.Change(DeviceTraceLevel.Info, "color", _curveKey, _curve is null
                ? "The display holds a tone curve WSGM did not write; brightness, contrast and gamma are unknown."
                : "The display's tone curve is the identity or WSGM's own.");
        }
        else
        {
            _result = curveResult;
        }

        if (_matrixBlock is null)
        {
            return;
        }

        var matrixResult = ReadMatrix();
        if (matrixResult == IgclResult.Success)
        {
            _matrix = ColorMath.Matches(IdentityMatrix, _coefficients)
                ? neutral
                : stored is { } record && ColorMath.Matches(_recordedMatrix, _coefficients)
                    ? record
                    : null;
            _log.Change(DeviceTraceLevel.Info, "color", _matrixKey, _matrix is null
                ? "The display holds a colour matrix WSGM did not write; hue and saturation are unknown."
                : "The display's colour matrix is the identity or WSGM's own.");
        }
        else if (_result == IgclResult.Success)
        {
            _result = matrixResult;
        }
    }

    /// <summary>The recorded settings, with their curve and matrix rebuilt only when the record changed.</summary>
    private ColorSettings? Recorded()
    {
        var stored = _store.Get(_display);
        if (stored is { } record && stored != _recorded)
        {
            ColorMath.FillCurve(record, _recordedCurve);
            ColorMath.HueSaturationMatrix(record).CopyTo(_recordedMatrix, 0);
        }

        _recorded = stored;
        return stored;
    }

    private int ReadCurve()
    {
        var block = _lutBlock;
        block.Size = (uint)sizeof(CtlPixTxBlockConfig);
        fixed (double* values = _samples)
        {
            block.Config.OneDLut.SampleValues = (nint)values;
            block.Config.OneDLut.SamplePositions = 0;
            var result = Query(&block);
            if (result != IgclResult.DataNotFound)
            {
                return result;
            }

            // Nothing set yet is the driver's own curve, which is the neutral one.
            _neutralCurve.CopyTo(_samples, 0);
            return IgclResult.Success;
        }
    }

    private int ReadMatrix()
    {
        var block = _matrixBlock!.Value;
        block.Size = (uint)sizeof(CtlPixTxBlockConfig);
        var result = Query(&block);
        if (result == IgclResult.DataNotFound)
        {
            IdentityMatrix.CopyTo(_coefficients, 0);
            return IgclResult.Success;
        }

        if (result != IgclResult.Success)
        {
            return result;
        }

        for (var index = 0; index < MatrixSize; index++)
        {
            _coefficients[index] = block.Config.Matrix.Matrix[index];
        }

        return result;
    }

    private int Query(CtlPixTxBlockConfig* block)
    {
        CtlPixTxPipeGetConfig request = default;
        request.QueryType = QueryCurrent;
        request.NumBlocks = 1;
        request.BlockConfigs = (nint)block;
        return _session.Call(_session.Api.PixTxGetConfig, _output.Handle, ref request);
    }

    private int WriteCurve(ColorSettings settings, WriteAdmission admission)
    {
        var lut = _lutBlock.Config.OneDLut;
        var perChannel = (int)lut.SamplesPerChannel;
        for (var channel = 0; channel < lut.Channels; channel++)
        {
            ColorMath.FillCurve(settings, _samples.AsSpan(channel * perChannel, perChannel));
        }

        var block = _lutBlock;
        block.Size = (uint)sizeof(CtlPixTxBlockConfig);
        fixed (double* values = _samples)
        {
            block.Config.OneDLut.SampleValues = (nint)values;
            block.Config.OneDLut.SamplePositions = 0;
            block.Config.OneDLut.SamplingType = SamplingUniform;
            return Apply(&block, admission);
        }
    }

    private int WriteMatrix(ColorSettings settings, WriteAdmission admission)
    {
        var block = _matrixBlock!.Value;
        block.Size = (uint)sizeof(CtlPixTxBlockConfig);
        var coefficients = ColorMath.HueSaturationMatrix(settings);
        for (var index = 0; index < 3; index++)
        {
            block.Config.Matrix.PreOffsets[index] = 0;
            block.Config.Matrix.PostOffsets[index] = 0;
        }

        for (var index = 0; index < MatrixSize; index++)
        {
            block.Config.Matrix.Matrix[index] = coefficients[index];
        }

        return Apply(&block, admission);
    }

    private int Apply(CtlPixTxBlockConfig* block, WriteAdmission admission, uint flags = FlagPersist)
    {
        CtlPixTxPipeSetConfig request = default;
        request.OperationType = OperationSetCustom;
        request.Flags = flags;
        request.NumBlocks = 1;
        request.BlockConfigs = (nint)block;
        admission.Check();
        return _session.Call(_session.Api.PixTxSetConfig, _output.Handle, ref request);
    }
}

/// <summary>One colour value of one display.</summary>
internal sealed class ColorControl : IntelControl
{
    private readonly ColorField _field;
    private readonly ColorPipeline _pipeline;

    private ColorControl(ColorPipeline pipeline, ColorField field, CapabilityDescriptor descriptor)
        : base(descriptor)
    {
        _pipeline = pipeline;
        _field = field;
        SupportKey = pipeline.SupportKey(field.InMatrix);
    }

    /// <inheritdoc />
    public override string SupportKey { get; }

    /// <inheritdoc />
    public override ControlWrite ProbeSupport(WriteAdmission admission)
    {
        return _pipeline.ProbeSupport(_field.InMatrix, admission);
    }

    /// <summary>Builds the colour controls of one display.</summary>
    /// <param name="pipeline">The pipe.</param>
    /// <param name="instance">The display's instance id.</param>
    /// <param name="placement">Where they sit.</param>
    /// <returns>The controls.</returns>
    public static IReadOnlyList<ColorControl> Build(ColorPipeline pipeline, string instance, Placement placement)
    {
        List<ColorControl> controls = [];
        foreach (var field in ColorField.All)
        {
            if (field.InMatrix && !pipeline.HasMatrix)
            {
                continue;
            }

            controls.Add(new ColorControl(pipeline, field,
                Descriptors.Range(field.Id, instance, field.Label, field.Range, field.Unit,
                    placement.Plus(controls.Count))));
        }

        return controls;
    }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        return _pipeline.Read(_field);
    }

    /// <inheritdoc />
    protected override ControlWrite WriteValidated(CapabilityValue value, WriteAdmission admission)
    {
        return _pipeline.Write(_field, value.IntegerValue!.Value, admission);
    }
}
