using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WinKeys = System.Windows.Forms.Keys;

namespace WSGM.Tools.HcDeviceExtract;

/// <summary>
///     Turns the decompiled Handheld Companion device classes into Device Lab knowledge records.
/// </summary>
/// <remarks>
///     The records are evidence, not drivers. The extractor reads only what HC states declaratively:
///     the device switch in <c>IDevice.GetCurrent</c>, top-level constructor assignments, OEM
///     chords, and the IMU JSON HC actually loads. Behaviour inside methods (HID layouts, init
///     sequences, EC writes in overrides) is listed as overridden members for a person to curate.
/// </remarks>
internal static class Program
{
    private const string DevicesNamespace = "HandheldCompanion.Devices";

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(
                "usage: HcDeviceExtract --source <HC source dir> --resources <HC Resources/Devices dir> "
                + "--output <knowledge dir> --hc-version <version>");
            return 64;
        }

        var classes = DeviceClass.Load(options.Source);
        var rules = IdentityExtractor.Extract(classes);
        var configurations = ImuConfiguration.Load(options.Resources, classes);
        var written = 0;
        foreach (var existing in Directory.EnumerateFiles(options.Output, "hc.*.json"))
        {
            File.Delete(existing);
        }

        // DefaultDevice is HC's catch-all for unknown hardware; it has no identity to match.
        foreach (var (className, classRules) in rules.Where(pair => pair.Key != "DefaultDevice")
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!classes.TryGetValue(className, out var deviceClass))
            {
                Console.Error.WriteLine($"warning: {className} is constructed by GetCurrent but not found");
                continue;
            }

            var record = RecordBuilder.Build(deviceClass, classes, classRules, configurations, options.HcVersion);
            var path = Path.Combine(options.Output, $"{record["id"]!.GetValue<string>()}.json");
            File.WriteAllText(path, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            written++;
        }

        Console.WriteLine($"Wrote {written} extracted records to {options.Output}");
        return 0;
    }

    internal static string Kebab(string name) => Words(name, '-', lower: true);

    internal static string Spaced(string name) => Words(name, ' ', lower: false);

    private static string Words(string name, char separator, bool lower)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '_')
            {
                builder.Append(separator);
                continue;
            }

            var boundary = i > 0 && char.IsUpper(c)
                           && (char.IsLower(name[i - 1])
                               || char.IsDigit(name[i - 1])
                               || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (boundary && builder.Length > 0 && builder[^1] != separator)
            {
                builder.Append(separator);
            }

            builder.Append(lower ? char.ToLowerInvariant(c) : c);
        }

        return builder.ToString();
    }

    internal static bool IsDevicesNamespace(string ns) =>
        ns == DevicesNamespace || ns.StartsWith(DevicesNamespace + ".", StringComparison.Ordinal);

    internal static bool IsExactDevicesNamespace(string ns) => ns == DevicesNamespace;
}

internal sealed record Options(string Source, string Resources, string Output, string HcVersion)
{
    public static Options? Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var source = Value("--source");
        var resources = Value("--resources");
        var output = Value("--output");
        var version = Value("--hc-version");
        return source is null || resources is null || output is null || version is null
            ? null
            : new Options(Path.GetFullPath(source), Path.GetFullPath(resources), Path.GetFullPath(output), version);
    }
}

/// <summary>One class in HC's device namespaces.</summary>
internal sealed record DeviceClass(
    string Name,
    string Namespace,
    string? BaseName,
    ClassDeclarationSyntax Declaration,
    string RelativePath)
{
    public static Dictionary<string, DeviceClass> Load(string sourceRoot)
    {
        Dictionary<string, DeviceClass> classes = new(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: relative);
            var root = tree.GetCompilationUnitRoot();
            var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
            if (ns is null || !Program.IsDevicesNamespace(ns))
            {
                continue;
            }

            foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (declaration.Parent is ClassDeclarationSyntax)
                {
                    continue;
                }

                var baseName = declaration.BaseList?.Types
                    .Select(type => type.Type)
                    .Select(type => type is QualifiedNameSyntax qualified ? qualified.Right.Identifier.Text : type.ToString())
                    .FirstOrDefault(name => !name.StartsWith('I') || name == "IDevice");
                classes.TryAdd(declaration.Identifier.Text,
                    new DeviceClass(declaration.Identifier.Text, ns, baseName, declaration, relative));
            }
        }

        return classes;
    }

    /// <summary>The class and its ancestors inside the device namespaces, root first.</summary>
    public IReadOnlyList<DeviceClass> Chain(IReadOnlyDictionary<string, DeviceClass> classes)
    {
        List<DeviceClass> chain = [this];
        var current = this;
        while (current.BaseName is { } baseName && classes.TryGetValue(baseName, out var parent) && !chain.Contains(parent))
        {
            chain.Add(parent);
            current = parent;
        }

        chain.Reverse();
        return chain;
    }

    public string Location(SyntaxNode node) =>
        $"{RelativePath}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
}

/// <summary>An identity rule in HC's terms.</summary>
internal sealed record IdentityRule(IReadOnlyDictionary<string, string> Fields, bool Fallback, string Location);

/// <summary>Reads the device switch in <c>IDevice.GetCurrent</c>.</summary>
internal static class IdentityExtractor
{
    // HC variable name to knowledge rule field.
    private static readonly Dictionary<string, string> Fields = new(StringComparer.Ordinal)
    {
        ["ManufacturerName"] = "baseboardManufacturer",
        ["ProductName"] = "baseboardProduct",
        ["SystemModel"] = "systemModel",
        ["SystemSKU"] = "systemSku",
        ["Processor"] = "processorName",
        ["Version"] = "baseboardVersion"
    };

    public static Dictionary<string, List<IdentityRule>> Extract(IReadOnlyDictionary<string, DeviceClass> classes)
    {
        var device = classes["IDevice"];
        var method = device.Declaration.Members.OfType<MethodDeclarationSyntax>()
            .Single(member => member.Identifier.Text == "GetCurrent");
        Dictionary<string, List<IdentityRule>> rules = new(StringComparer.Ordinal);
        Walk(method.Body!.Statements, new Context(device, rules, new Dictionary<string, string>(StringComparer.Ordinal)),
            ImmutableConditions.Empty);
        return rules;
    }

    private static void Walk(IEnumerable<StatementSyntax> statements, Context context, ImmutableConditions conditions)
    {
        foreach (var statement in statements)
        {
            Walk(statement, context, conditions);
        }
    }

    private static void Walk(StatementSyntax statement, Context context, ImmutableConditions conditions)
    {
        switch (statement)
        {
            case BlockSyntax block:
                Walk(block.Statements, context, conditions);
                break;
            case LocalDeclarationStatementSyntax local:
                foreach (var variable in local.Declaration.Variables)
                {
                    if (variable.Initializer?.Value is IdentifierNameSyntax alias
                        && context.Resolve(alias.Identifier.Text) is { } field)
                    {
                        context.Aliases[variable.Identifier.Text] = field;
                    }
                }

                break;
            case SwitchStatementSyntax switchStatement:
                WalkSwitch(switchStatement, context, conditions);
                break;
            case IfStatementSyntax ifStatement:
                WalkIf(ifStatement, context, conditions);
                break;
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
                when assignment.Left.ToString() == "device":
                Assign(assignment.Right, context, conditions);
                break;
        }
    }

    private static void WalkSwitch(SwitchStatementSyntax switchStatement, Context context, ImmutableConditions conditions)
    {
        var field = switchStatement.Expression is IdentifierNameSyntax name ? context.Resolve(name.Identifier.Text) : null;
        foreach (var section in switchStatement.Sections)
        {
            var statements = section.Statements.Where(statement => statement is not BreakStatementSyntax).ToArray();
            foreach (var label in section.Labels)
            {
                var sectionConditions = label switch
                {
                    CaseSwitchLabelSyntax { Value: LiteralExpressionSyntax literal } when field is not null =>
                        conditions.With(field, literal.Token.ValueText),
                    DefaultSwitchLabelSyntax => conditions.AsFallback(),
                    _ => null
                };
                if (sectionConditions is not null)
                {
                    Walk(statements, context, sectionConditions);
                }
            }
        }
    }

    private static void WalkIf(IfStatementSyntax ifStatement, Context context, ImmutableConditions conditions)
    {
        // Two shapes appear: `if (X == "a")` and the decompiled `if (!(X == "a")) {...} else {...}`.
        var negated = ifStatement.Condition is PrefixUnaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.LogicalNotExpression,
            Operand: ParenthesizedExpressionSyntax parenthesized
        };
        var comparison = negated
            ? ((ParenthesizedExpressionSyntax)((PrefixUnaryExpressionSyntax)ifStatement.Condition).Operand).Expression
            : ifStatement.Condition;
        if (comparison is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } equals
            && equals.Left is IdentifierNameSyntax left
            && context.Resolve(left.Identifier.Text) is { } field
            && equals.Right is LiteralExpressionSyntax literal)
        {
            var matched = conditions.With(field, literal.Token.ValueText);
            Walk(ifStatement.Statement, context, negated ? conditions : matched);
            if (ifStatement.Else is not null)
            {
                Walk(ifStatement.Else.Statement, context, negated ? matched : conditions);
            }

            return;
        }

        Walk(ifStatement.Statement, context, conditions);
        if (ifStatement.Else is not null)
        {
            Walk(ifStatement.Else.Statement, context, conditions);
        }
    }

    private static void Assign(ExpressionSyntax value, Context context, ImmutableConditions conditions)
    {
        switch (value)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                Assign(parenthesized.Expression, context, conditions);
                break;
            case ObjectCreationExpressionSyntax creation:
                context.Add(TypeName(creation.Type), conditions, creation);
                break;
            case ConditionalExpressionSyntax conditional:
                // `Processor.Contains("4500U") ? new A() : new B()`: A needs the substring; B is
                // what that branch falls back to, so it ranks below any exact match.
                if (conditional.Condition is InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Contains", Expression: IdentifierNameSyntax target },
                        ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax needle }]
                    }
                    && context.Resolve(target.Identifier.Text) == "processorName")
                {
                    Assign(conditional.WhenTrue, context, conditions.With("processorNameContains", needle.Token.ValueText));
                    Assign(conditional.WhenFalse, context, conditions.AsFallback());
                }

                break;
        }
    }

    private static string TypeName(TypeSyntax type) =>
        type is QualifiedNameSyntax qualified ? qualified.Right.Identifier.Text : type.ToString();

    private sealed record Context(
        DeviceClass Device,
        Dictionary<string, List<IdentityRule>> Rules,
        Dictionary<string, string> Aliases)
    {
        public string? Resolve(string name) =>
            Fields.TryGetValue(name, out var field) ? field : Aliases.GetValueOrDefault(name);

        public void Add(string className, ImmutableConditions conditions, SyntaxNode node)
        {
            if (!Rules.TryGetValue(className, out var list))
            {
                Rules[className] = list = [];
            }

            list.Add(new IdentityRule(conditions.Fields, conditions.Fallback, Device.Location(node)));
        }
    }

    private sealed record ImmutableConditions(IReadOnlyDictionary<string, string> Fields, bool Fallback)
    {
        public static readonly ImmutableConditions Empty = new(new Dictionary<string, string>(), false);

        public ImmutableConditions With(string field, string value) =>
            this with { Fields = new Dictionary<string, string>(Fields) { [field] = value } };

        public ImmutableConditions AsFallback() => this with { Fallback = true };
    }
}

/// <summary>The IMU configuration HC applies to one class, reproduced from its loader.</summary>
internal static class ImuConfiguration
{
    public static Dictionary<string, JsonObject> Load(string directory, IReadOnlyDictionary<string, DeviceClass> classes)
    {
        Dictionary<string, JsonObject> files = new(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            files[Path.GetFileNameWithoutExtension(file)] = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        }

        return files;
    }

    /// <summary>
    ///     Reproduces <c>DeviceConfigurationHelper.LoadConfigurationRecursive</c>: the class's own
    ///     file, then the parent's, but the parent is found with
    ///     <c>Type.GetType("HandheldCompanion.Devices." + name)</c>, so the walk stops at any class
    ///     outside that exact namespace.
    /// </summary>
    public static (string? File, JsonObject? Json) Applied(
        DeviceClass deviceClass,
        IReadOnlyDictionary<string, DeviceClass> classes,
        IReadOnlyDictionary<string, JsonObject> files)
    {
        var current = deviceClass;
        while (true)
        {
            if (files.TryGetValue(current.Name, out var json))
            {
                return (current.Name, json);
            }

            if (!Program.IsExactDevicesNamespace(current.Namespace)
                || current.BaseName is not { } baseName
                || !classes.TryGetValue(baseName, out var parent)
                || baseName == "IDevice")
            {
                return (null, null);
            }

            current = parent;
        }
    }
}

/// <summary>Evaluates top-level constructor statements across a class chain.</summary>
internal sealed class ConstructorState
{
    private readonly Dictionary<string, List<string>> _keyLists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _spans = new(StringComparer.Ordinal);
    private readonly HashSet<string> _chordAliases = new(StringComparer.Ordinal) { "OEMChords" };

    public int? VendorId { get; private set; }
    public List<int> ProductIds { get; private set; } = [];
    public List<(int ProductId, int UsagePage, int Usage)> HidFilters { get; private set; } = [];
    public double[]? NominalWatts { get; private set; }
    public double[]? ConfigurableWatts { get; private set; }
    public double[]? GpuClock { get; private set; }
    public int? CpuClock { get; private set; }
    public bool UseOpenLib { get; private set; }
    public SortedSet<string> Capabilities { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> LightingModes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int>? EcDetails { get; private set; }
    public string? EcLocation { get; private set; }
    public List<Chord> Chords { get; } = [];
    public string? HidLocation { get; private set; }

    public void Apply(DeviceClass deviceClass)
    {
        var constructor = deviceClass.Declaration.Members.OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault(member => member.ParameterList.Parameters.Count == 0
                                      && !member.Modifiers.Any(SyntaxKind.StaticKeyword));
        if (constructor?.Body is null)
        {
            return;
        }

        _keyLists.Clear();
        _spans.Clear();
        _chordAliases.Clear();
        _chordAliases.Add("OEMChords");
        foreach (var statement in constructor.Body.Statements)
        {
            Statement(statement, deviceClass);
        }
    }

    private void Statement(StatementSyntax statement, DeviceClass deviceClass)
    {
        switch (statement)
        {
            case LocalDeclarationStatementSyntax local:
                foreach (var variable in local.Declaration.Variables)
                {
                    Local(variable);
                }

                break;
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }:
                Assignment(assignment, deviceClass);
                break;
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation }:
                Invocation(invocation, deviceClass);
                break;
        }
    }

    private void Local(VariableDeclaratorSyntax variable)
    {
        var name = variable.Identifier.Text;
        switch (variable.Initializer?.Value)
        {
            case IdentifierNameSyntax { Identifier.Text: "OEMChords" }:
                _chordAliases.Add(name);
                break;
            case ObjectCreationExpressionSyntax creation when creation.Type.ToString() == "List<KeyCode>":
                _keyLists[name] = Keys(creation.Initializer);
                break;
            case InvocationExpressionSyntax invocation when SpanTarget(invocation) is { } list:
                _spans[name] = list;
                break;
        }
    }

    private void Assignment(AssignmentExpressionSyntax assignment, DeviceClass deviceClass)
    {
        if (assignment.Left is ElementAccessExpressionSyntax element)
        {
            // `span[0] = KeyCode.F11` or `CollectionsMarshal.AsSpan(list)[0] = KeyCode.F18`.
            var list = element.Expression switch
            {
                IdentifierNameSyntax span when _spans.TryGetValue(span.Identifier.Text, out var target) => target,
                InvocationExpressionSyntax invocation => SpanTarget(invocation),
                _ => null
            };
            if (list is not null && _keyLists.TryGetValue(list, out var keys)
                && element.ArgumentList.Arguments is [{ Expression: LiteralExpressionSyntax index }])
            {
                var position = (int)index.Token.Value!;
                while (keys.Count <= position)
                {
                    keys.Add("?");
                }

                keys[position] = Key(assignment.Right);
            }

            return;
        }

        var name = assignment.Left switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => member.Name.Identifier.Text,
            _ => null
        };
        var kind = assignment.Kind();
        switch (name)
        {
            case "vendorId" when Number(assignment.Right) is { } vendor:
                VendorId = (int)vendor;
                HidLocation = deviceClass.Location(assignment);
                break;
            case "productIds":
                ProductIds = [.. Numbers(assignment.Right).Select(value => (int)value)];
                HidLocation = deviceClass.Location(assignment);
                break;
            case "hidFilters":
                HidFilters = Filters(assignment.Right);
                break;
            case "nTDP":
                NominalWatts = Numbers(assignment.Right);
                break;
            case "cTDP":
                ConfigurableWatts = Numbers(assignment.Right);
                break;
            case "GfxClock":
                GpuClock = Numbers(assignment.Right);
                break;
            case "CpuClock" when Number(assignment.Right) is { } clock:
                CpuClock = (int)clock;
                break;
            case "UseOpenLib":
                UseOpenLib = assignment.Right.IsKind(SyntaxKind.TrueLiteralExpression);
                break;
            case "Capabilities":
                Flags(Capabilities, kind, assignment.Right);
                break;
            case "DynamicLightingCapabilities":
                Flags(LightingModes, kind, assignment.Right);
                break;
            case "ECDetails" when assignment.Right is ObjectCreationExpressionSyntax creation:
                EcDetails = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var member in creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>() ?? [])
                {
                    if (Number(member.Right) is { } number)
                    {
                        EcDetails[member.Left.ToString()] = (int)number;
                    }
                }

                EcLocation = deviceClass.Location(assignment);
                break;
        }
    }

    private void Invocation(InvocationExpressionSyntax invocation, DeviceClass deviceClass)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax target,
                Name.Identifier.Text: var method
            }
            || !_chordAliases.Contains(target.Identifier.Text))
        {
            return;
        }

        if (method == "Clear")
        {
            Chords.Clear();
            return;
        }

        if (method != "Add"
            || invocation.ArgumentList.Arguments is not [{ Expression: ObjectCreationExpressionSyntax creation }]
            || creation.Type.ToString() != "KeyboardChord"
            || creation.ArgumentList?.Arguments is not { Count: >= 5 } arguments)
        {
            return;
        }

        var name = arguments[0].Expression is LiteralExpressionSyntax literal ? literal.Token.ValueText : "?";
        var silenced = arguments[3].Expression.IsKind(SyntaxKind.TrueLiteralExpression);
        var flag = arguments[4].Expression is MemberAccessExpressionSyntax flagAccess
            ? flagAccess.Name.Identifier.Text
            : arguments[4].Expression.ToString();
        Chords.Add(new Chord(name, KeyList(arguments[1].Expression), KeyList(arguments[2].Expression), silenced, flag,
            deviceClass.Location(invocation)));
    }

    private List<string> KeyList(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier when _keyLists.TryGetValue(identifier.Identifier.Text, out var keys) => [.. keys],
        LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } => [],
        ObjectCreationExpressionSyntax creation => Keys(creation.Initializer),
        CollectionExpressionSyntax collection => [.. collection.Elements.OfType<ExpressionElementSyntax>().Select(element => Key(element.Expression))],
        _ => ["?"]
    };

    private static List<string> Keys(InitializerExpressionSyntax? initializer) =>
        initializer is null ? [] : [.. initializer.Expressions.Select(Key)];

    // HC's KeyCode values are Win32 virtual keys. The decompiler prints a code with no member of its
    // own as an OR of members (VK 0x07 as `LButton | XButton2`), so those become hex.
    internal static string Key(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case MemberAccessExpressionSyntax member:
                return member.Name.Identifier.Text;
            case ParenthesizedExpressionSyntax parenthesized:
                return Key(parenthesized.Expression);
            case CastExpressionSyntax { Expression: var inner } when Number(inner) is { } cast:
                return $"0x{(int)cast:X2}";
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.BitwiseOrExpression }:
                return VirtualKey(expression) is { } value ? $"0x{value:X2}" : expression.ToString();
            default:
                return expression.ToString();
        }
    }

    private static int? VirtualKey(ExpressionSyntax expression) => expression switch
    {
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.BitwiseOrExpression } or =>
            VirtualKey(or.Left) is { } left && VirtualKey(or.Right) is { } right ? left | right : null,
        ParenthesizedExpressionSyntax parenthesized => VirtualKey(parenthesized.Expression),
        MemberAccessExpressionSyntax member when Enum.TryParse<WinKeys>(member.Name.Identifier.Text, out var key) =>
            (int)key,
        _ => null
    };

    private static string? SpanTarget(InvocationExpressionSyntax invocation) =>
        invocation is
        {
            Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "AsSpan" },
            ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax list }]
        }
            ? list.Identifier.Text
            : null;

    private static void Flags(SortedSet<string> set, SyntaxKind kind, ExpressionSyntax value)
    {
        var names = FlagNames(value).ToArray();
        switch (kind)
        {
            case SyntaxKind.SimpleAssignmentExpression:
                set.Clear();
                set.UnionWith(names.Where(name => name != "None"));
                break;
            case SyntaxKind.OrAssignmentExpression:
                set.UnionWith(names);
                break;
            case SyntaxKind.AndAssignmentExpression:
                set.ExceptWith(names);
                break;
        }
    }

    private static IEnumerable<string> FlagNames(ExpressionSyntax value) => value switch
    {
        MemberAccessExpressionSyntax member => [member.Name.Identifier.Text],
        BinaryExpressionSyntax binary => FlagNames(binary.Left).Concat(FlagNames(binary.Right)),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.BitwiseNotExpression } not => FlagNames(not.Operand),
        ParenthesizedExpressionSyntax parenthesized => FlagNames(parenthesized.Expression),
        _ => []
    };

    private static List<(int, int, int)> Filters(ExpressionSyntax value)
    {
        List<(int, int, int)> filters = [];
        if (value is not ObjectCreationExpressionSyntax { Initializer: { } initializer })
        {
            return filters;
        }

        foreach (var entry in initializer.Expressions.OfType<InitializerExpressionSyntax>())
        {
            if (entry.Expressions is [var key, ObjectCreationExpressionSyntax { ArgumentList.Arguments: [var page, var usage] }]
                && Number(key) is { } productId
                && Number(page.Expression) is { } usagePage
                && Number(usage.Expression) is { } usageId)
            {
                filters.Add(((int)productId, (int)usagePage & 0xFFFF, (int)usageId & 0xFFFF));
            }
        }

        return filters;
    }

    internal static double[] Numbers(ExpressionSyntax value)
    {
        var initializer = value switch
        {
            ArrayCreationExpressionSyntax array => array.Initializer,
            ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer,
            InitializerExpressionSyntax direct => direct,
            _ => null
        };
        return initializer is null
            ? []
            : [.. initializer.Expressions.Select(Number).Where(number => number.HasValue).Select(number => number!.Value)];
    }

    internal static double? Number(ExpressionSyntax value) => value switch
    {
        LiteralExpressionSyntax literal when literal.Token.Value is IConvertible convertible and not string =>
            convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } negative =>
            -Number(negative.Operand),
        CastExpressionSyntax cast => Number(cast.Expression),
        ParenthesizedExpressionSyntax parenthesized => Number(parenthesized.Expression),
        _ => null
    };
}

internal sealed record Chord(
    string Name,
    List<string> PressKeys,
    List<string> ReleaseKeys,
    bool Silenced,
    string Flag,
    string Location);

/// <summary>Assembles one knowledge record as JSON.</summary>
internal static class RecordBuilder
{
    // Behaviour the survey found in HC that the wizard must not copy, keyed by class. The
    // extractor cannot see these in constructors, so they are listed here with their source.
    private static readonly Dictionary<string, string[]> Hazards = new(StringComparer.Ordinal)
    {
        ["LegionGoTablet"] =
        [
            "HC clips Windows-sensor gyro readings at 124 deg/s (GyroThreshold); fast rotations read as zero on that path. Source: HandheldCompanion.Devices.Lenovo/LegionGoTablet.cs."
        ],
        ["LegionGoTablet2"] =
        [
            "Inherits the LegionGoTablet 124 deg/s gyro clip on the Windows-sensor path."
        ],
        ["ClawA1M"] =
        [
            "HC's Open() writes PL1/PL2 as a side effect (35/35 W, then 30/37 W on A2VM/CG3EM). Never replay it as a probe. Source: HandheldCompanion.Devices/ClawA1M.cs."
        ],
        ["GPDWinMini"] =
        [
            "HC's ReadFanDuty reads raw I/O ports 0x78/0x79, not EC registers; its fan readback is not evidence."
        ],
        ["AynLoki"] =
        [
            "HC's ReadFanDuty reads I/O ports 0x20/0x21 (the interrupt controller) and 0xB3; its fan readback is not evidence."
        ],
        ["OneXPlayerX1"] =
        [
            "The X1 opens its own CH340 serial port (115200) for LED control. Do not probe it as a serial IMU."
        ],
        ["SteamDeck"] =
        [
            "HC's OEM TDP path passes watts where VangoghGPU expects milliwatts, so every value clamps to 3 W. Never replay it."
        ]
    };

    private static readonly string[] Axes = ["X", "Y", "Z"];

    public static JsonObject Build(
        DeviceClass deviceClass,
        IReadOnlyDictionary<string, DeviceClass> classes,
        IReadOnlyList<IdentityRule> rules,
        IReadOnlyDictionary<string, JsonObject> imuFiles,
        string hcVersion)
    {
        var chain = deviceClass.Chain(classes);
        var state = new ConstructorState();
        foreach (var member in chain)
        {
            state.Apply(member);
        }

        var id = "hc." + Program.Kebab(deviceClass.Name);
        var reference = $"_ref/HandheldCompanion {hcVersion}";
        JsonObject record = new()
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["displayName"] = Program.Spaced(deviceClass.Name),
            ["status"] = "Extracted",
            ["hcClass"] = deviceClass.Name
        };
        var bases = chain.Take(chain.Count - 1).Reverse().Select(item => item.Name).Where(name => name != "IDevice").ToArray();
        if (bases.Length > 0)
        {
            record["hcBaseClasses"] = Array(bases);
        }

        record["identity"] = new JsonArray([.. rules.Select(Rule)]);
        var power = Power(state);
        if (power is not null)
        {
            record["power"] = power;
        }

        if (state.Capabilities.Count > 0)
        {
            record["capabilities"] = Array(state.Capabilities);
        }

        if (state.LightingModes.Count > 0)
        {
            record["lightingModes"] = Array(state.LightingModes);
        }

        var endpoints = Endpoints(state, reference);
        if (endpoints.Count > 0)
        {
            record["hidEndpoints"] = endpoints;
        }

        if (state.Chords.Count > 0)
        {
            record["buttons"] = new JsonArray([.. state.Chords.Select(chord => Button(chord, reference))]);
        }

        List<string> hazards = [];
        var motion = Motion(deviceClass, classes, imuFiles, reference, hazards);
        if (motion is not null)
        {
            record["motion"] = motion;
        }

        var overrides = Overrides(chain);
        var mechanisms = Mechanisms(state, overrides, reference);
        if (mechanisms.Count > 0)
        {
            record["mechanisms"] = mechanisms;
        }

        if (overrides.Count > 0)
        {
            record["hcOverrides"] = Array(overrides);
        }

        foreach (var member in chain)
        {
            hazards.AddRange(Hazards.GetValueOrDefault(member.Name, []));
        }

        if (hazards.Count > 0)
        {
            record["hazards"] = Array(hazards);
        }

        record["provenance"] = Provenance(reference + ": " + deviceClass.RelativePath,
            "Extracted by tools/HcDeviceExtract from constructors and IDevice.GetCurrent; nothing here is hardware-verified.");
        return record;
    }

    private static JsonObject Rule(IdentityRule rule)
    {
        JsonObject json = [];
        foreach (var field in new[]
                 {
                     "baseboardManufacturer", "baseboardProduct", "systemModel", "systemSku", "processorName",
                     "processorNameContains", "baseboardVersion"
                 })
        {
            if (rule.Fields.TryGetValue(field, out var value))
            {
                json[field] = value;
            }
        }

        if (rule.Fallback)
        {
            json["fallback"] = true;
        }

        return json;
    }

    private static JsonObject? Power(ConstructorState state)
    {
        JsonObject power = [];
        if (state.NominalWatts is { Length: > 0 } nominal)
        {
            power["nominalWatts"] = Array(nominal);
        }

        if (state.ConfigurableWatts is { Length: > 0 } configurable)
        {
            power["configurableWatts"] = Array(configurable);
        }

        if (state.GpuClock is { Length: > 0 } gpu)
        {
            power["gpuClockMhz"] = Array(gpu);
        }

        if (state.CpuClock is { } cpu)
        {
            power["cpuClockMhz"] = cpu;
        }

        return power.Count > 0 ? power : null;
    }

    private static JsonArray Endpoints(ConstructorState state, string reference)
    {
        JsonArray endpoints = [];
        if (state.VendorId is not { } vendor)
        {
            return endpoints;
        }

        var provenance = Provenance(reference + ": " + state.HidLocation, null)[0];
        if (state.HidFilters.Count == 0)
        {
            endpoints.Add(new JsonObject
            {
                ["role"] = "vendor",
                ["vendorId"] = Hex4(vendor),
                ["productIds"] = new JsonArray([.. state.ProductIds.Select(pid => (JsonNode)Hex4(pid))]),
                ["provenance"] = provenance!.DeepClone()
            });
            return endpoints;
        }

        foreach (var group in state.HidFilters.GroupBy(filter => (filter.UsagePage, filter.Usage)))
        {
            endpoints.Add(new JsonObject
            {
                ["role"] = "vendor",
                ["vendorId"] = Hex4(vendor),
                ["productIds"] = new JsonArray([.. group.Select(filter => (JsonNode)Hex4(filter.ProductId))]),
                ["usagePage"] = group.Key.UsagePage,
                ["usage"] = group.Key.Usage,
                ["provenance"] = provenance!.DeepClone()
            });
        }

        return endpoints;
    }

    private static JsonObject Button(Chord chord, string reference)
    {
        var declared = chord.PressKeys.Count == 0 && chord.ReleaseKeys.Count == 0;
        JsonObject button = new()
        {
            ["name"] = chord.Name,
            ["hcFlag"] = chord.Flag,
            ["source"] = declared ? "Declared" : "KeyboardChord"
        };
        if (chord.PressKeys.Count > 0)
        {
            button["pressKeys"] = Array(chord.PressKeys);
        }

        if (chord.ReleaseKeys.Count > 0)
        {
            button["releaseKeys"] = Array(chord.ReleaseKeys);
        }

        if (chord.Silenced)
        {
            button["silenced"] = true;
        }

        button["provenance"] = Provenance(reference + ": " + chord.Location,
            declared ? "HC declares the button with no keys; its real source is read elsewhere in the class." : null)[0]!.DeepClone();
        return button;
    }

    private static JsonObject? Motion(
        DeviceClass deviceClass,
        IReadOnlyDictionary<string, DeviceClass> classes,
        IReadOnlyDictionary<string, JsonObject> imuFiles,
        string reference,
        List<string> hazards)
    {
        var (file, json) = ImuConfiguration.Applied(deviceClass, classes, imuFiles);

        // A file named after this class with hyphens instead of underscores is never loaded.
        var unloaded = imuFiles.Keys.FirstOrDefault(name =>
            name != deviceClass.Name && name.Replace('-', '_') == deviceClass.Name);
        if (unloaded is not null)
        {
            hazards.Add($"HC ships Resources/Devices/{unloaded}.json for this device but never loads it (it looks up {deviceClass.Name}.json), so HC runs "
                        + (file is null ? "with identity axes." : $"with {file}.json instead."));
        }

        if (json is null)
        {
            if (Program.IsDevicesNamespace(deviceClass.Namespace) && !Program.IsExactDevicesNamespace(deviceClass.Namespace)
                                                                  && unloaded is null)
            {
                hazards.Add($"HC finds no IMU configuration for this class ({deviceClass.Namespace} is outside the namespace its parent lookup searches), so it applies identity axes.");
            }

            return null;
        }

        JsonObject motion = [];
        if (AxisMap(json, "GyroMatrix", hazards, "gyrometer") is { } gyro)
        {
            motion["gyrometer"] = gyro;
        }

        if (AxisMap(json, "AcceleroMatrix", hazards, "accelerometer") is { } accel)
        {
            motion["accelerometer"] = accel;
        }

        JsonArray legacy = [];
        foreach (var (property, kind) in new[] { ("WindowsGyrometerFields", "gyrometer"), ("WindowsAccelerometerFields", "accelerometer") })
        {
            if (json[property] is JsonObject fields)
            {
                legacy.Add(new JsonObject
                {
                    ["kind"] = kind,
                    ["friendlyName"] = fields["FriendlyName"]?.GetValue<string>() ?? string.Empty,
                    ["formatId"] = fields["FormatId"]?.GetValue<string>() ?? string.Empty,
                    ["propertyIds"] = new JsonArray([.. (fields["PropertyIds"]?.AsArray() ?? []).Select(id => (JsonNode)id!.GetValue<int>())])
                });
            }
        }

        if (legacy.Count > 0)
        {
            motion["legacyFields"] = legacy;
        }

        motion["provenance"] = Provenance($"{reference}: Resources/Devices/{file}.json",
            file == deviceClass.Name ? null : $"Inherited from {file}.json through HC's parent lookup.")[0]!.DeepClone();
        return motion;
    }

    private static JsonObject? AxisMap(JsonObject json, string property, List<string> hazards, string kind)
    {
        if (json[property] is not JsonObject matrix
            || matrix["Axis"] is not JsonObject axis
            || matrix["AxisSwap"] is not JsonObject swap)
        {
            return null;
        }

        JsonObject swapJson = [];
        JsonObject signJson = [];
        foreach (var name in Axes)
        {
            swapJson[name] = swap[name]?.GetValue<string>() ?? name;
            signJson[name] = axis[name]?.GetValue<double>() < 0 ? -1 : 1;
        }

        var targets = Axes.Select(name => swapJson[name]!.GetValue<string>()).ToArray();
        if (targets.Distinct(StringComparer.Ordinal).Count() != 3)
        {
            hazards.Add($"HC's {kind} axis swap sends two raw axes to the same output ({string.Join(", ", Axes.Select((name, i) => $"{name}->{targets[i]}"))}); one axis is lost.");
        }

        return new JsonObject { ["swap"] = swapJson, ["sign"] = signJson };
    }

    private static List<string> Overrides(IReadOnlyList<DeviceClass> chain)
    {
        SortedSet<string> overrides = new(StringComparer.Ordinal);
        foreach (var member in chain.Where(item => item.Name != "IDevice"))
        {
            foreach (var method in member.Declaration.Members.OfType<MethodDeclarationSyntax>()
                         .Where(method => method.Modifiers.Any(SyntaxKind.OverrideKeyword)))
            {
                overrides.Add($"{member.Name}.{method.Identifier.Text}");
            }
        }

        return [.. overrides];
    }

    private static JsonArray Mechanisms(ConstructorState state, List<string> overrides, string reference)
    {
        JsonArray mechanisms = [];
        if (state.EcDetails is not { } ec || !ec.TryGetValue("AddressFanDuty", out var duty) || duty == 0)
        {
            return mechanisms;
        }

        var index = ec.GetValueOrDefault("AddressStatusCommandPort");
        var fanOverride = overrides.Where(name => name.EndsWith(".SetFanDuty", StringComparison.Ordinal)
                                                  || name.EndsWith(".SetFanControl", StringComparison.Ordinal)).ToArray();
        var parameters = new JsonObject
        {
            ["indexPort"] = Hex(index),
            ["dataPort"] = Hex(ec.GetValueOrDefault("AddressDataPort")),
            ["controlRegister"] = Hex(ec.GetValueOrDefault("AddressFanControl")),
            ["dutyRegister"] = Hex(duty),
            ["dutyMinimum"] = ec.GetValueOrDefault("FanValueMin").ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["dutyMaximum"] = ec.GetValueOrDefault("FanValueMax").ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        mechanisms.Add(new JsonObject
        {
            ["feature"] = "fan",
            ["transport"] = index is 0x4E or 0x2E ? "superio-ec" : "acpi-ec",
            ["parameters"] = parameters,
            ["note"] = fanOverride.Length > 0
                ? $"ECDetails as declared; {string.Join(" and ", fanOverride)} replace the default write, so check them before trusting this."
                : "ECDetails with HC's default SetFanDuty: duty = percent * (max - min) / 100 + min; control register 1 = manual, 0 = auto.",
            ["provenance"] = Provenance(reference + ": " + state.EcLocation, null)[0]!.DeepClone()
        });
        return mechanisms;
    }

    private static JsonArray Provenance(string reference, string? note)
    {
        JsonObject provenance = new() { ["source"] = "HcDerived", ["reference"] = reference };
        if (note is not null)
        {
            provenance["note"] = note;
        }

        return [provenance];
    }

    private static JsonArray Array(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode)value)]);

    private static JsonArray Array(IEnumerable<double> values) => new([.. values.Select(value => (JsonNode)value)]);

    private static string Hex4(int value) => value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);

    private static string Hex(int value) => "0x" + value.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
}
