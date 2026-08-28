using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.DeviceLab.Core.Application;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Gui;

internal sealed class MainWindow : Window
{
    private readonly DeviceLabApplication _application;
    private readonly ComboBox _mode;
    private readonly TabControl _tabs;
    private readonly TextBox _result;
    private readonly Button _cancel;
    private readonly IReadOnlyList<TabItem> _ownerTabs;
    private readonly IReadOnlyList<TabItem> _developerTabs;
    private CancellationTokenSource? _operation;
    private CaptureExportPlan? _captureExportPlan;
    private string? _reviewedRecipeHash;

    private static readonly JsonSerializerOptions DisplayJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public MainWindow()
    {
        string? repositoryRoot = DeviceLabRepositoryLocator.Find(Environment.CurrentDirectory)
            ?? DeviceLabRepositoryLocator.Find(AppContext.BaseDirectory);
        _application = new DeviceLabApplication(
            repositoryRoot,
            Path.Combine(AppContext.BaseDirectory, "WSGM.Device.ProbeHost.exe"));

        Title = "WSGM Device Lab";
        Width = 1180;
        Height = 800;
        MinWidth = 900;
        MinHeight = 620;

        _mode = new ComboBox
        {
            ItemsSource = new[] { "Hardware Owner", "Plugin Developer" },
            SelectedIndex = 0,
            Width = 190,
        };
        _mode.SelectionChanged += (_, _) => ApplyMode();
        _cancel = new Button { Content = "Cancel current operation", IsEnabled = false };
        _cancel.Click += (_, _) => _operation?.Cancel();

        TabItem safety = BuildSafetyTab();
        TabItem candidates = BuildCandidatesTab();
        TabItem capture = BuildCaptureTab();
        TabItem workbench = BuildWorkbenchTab();
        TabItem scaffold = BuildScaffoldTab();
        TabItem package = BuildPackageTab();
        _ownerTabs = [safety, candidates, capture, workbench];
        _developerTabs = [safety, candidates, capture, workbench, scaffold, package];
        _tabs = new TabControl { ItemsSource = _ownerTabs };

        _result = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 175,
            FontFamily = FontFamily.Default,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_result, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_result, ScrollBarVisibility.Auto);

        Grid root = new()
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,180"),
            RowSpacing = 10,
        };
        root.Children.Add(Header());
        Grid.SetRow(_tabs, 2);
        root.Children.Add(_tabs);
        TextBlock resultHeading = Heading("Result / evidence preview");
        Grid.SetRow(resultHeading, 3);
        root.Children.Add(resultHeading);
        Grid.SetRow(_result, 4);
        root.Children.Add(_result);
        Content = root;
    }

    private Control Header()
    {
        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 12,
        };
        StackPanel title = new() { Spacing = 3 };
        title.Children.Add(new TextBlock
        {
            Text = "WSGM Device Lab",
            FontSize = 25,
            FontWeight = FontWeight.SemiBold,
        });
        title.Children.Add(new TextBlock
        {
            Text = "Read-only by default. Imported files are evidence, never hardware authority.",
            Foreground = Brushes.Silver,
        });
        header.Children.Add(title);
        Grid.SetColumn(_mode, 1);
        header.Children.Add(_mode);
        Grid.SetColumn(_cancel, 2);
        header.Children.Add(_cancel);
        return header;
    }

    private TabItem BuildSafetyTab()
    {
        TextBox output = PathInput(DefaultOutputDirectory());
        CheckBox shareable = new()
        {
            Content = "Create a shareable inventory (redact unique identifiers)",
            IsChecked = true,
        };
        Button doctor = new() { Content = "Run doctor" };
        doctor.Click += async (_, _) =>
        {
            string outputPath = output.Text!;
            await RunAsync(token => Task.Run<object?>(
                () => _application.Doctor(outputPath, DateTimeOffset.UtcNow), token));
        };
        Button inventory = new() { Content = "Collect inventory" };
        inventory.Click += async (_, _) =>
        {
            string outputPath = output.Text!;
            bool sanitize = shareable.IsChecked is true;
            await RunAsync(token => Task.Run<object?>(() => _application.Inventory(
                outputPath,
                sanitize,
                DateTimeOffset.UtcNow), token));
        };
        return Tab(
            "Safety & inventory",
            "Review environment and output safety, then collect read-only machine inventory.",
            Labeled("Output directory", output),
            shareable,
            Buttons(doctor, inventory));
    }

    private TabItem BuildCandidatesTab()
    {
        TextBox inventoryPath = PathInput();
        TextBox deviceId = new() { PlaceholderText = "Optional exact logical device ID" };
        TextBox probeId = new() { PlaceholderText = "Reviewed probe ID from candidate output" };
        TextBox probeOutput = PathInput(DefaultOutputDirectory());
        Button assess = new() { Content = "Compare candidates and read probes" };
        assess.Click += async (_, _) =>
        {
            string inventoryFile = inventoryPath.Text!;
            string? targetDevice = string.IsNullOrWhiteSpace(deviceId.Text) ? null : deviceId.Text;
            await RunAsync(token => Task.Run<object?>(
                () => _application.Candidates(inventoryFile, targetDevice), token));
        };
        Button runProbe = new() { Content = "Run selected reviewed read probe" };
        runProbe.Click += async (_, _) =>
        {
            string inventoryFile = inventoryPath.Text!;
            string selectedProbe = probeId.Text!;
            string outputPath = probeOutput.Text!;
            await RunAsync(token => Task.Run<object?>(async () => await _application.RunReadProbeAsync(
                inventoryFile,
                selectedProbe,
                outputPath,
                token).ConfigureAwait(false), token));
        };
        return Tab(
            "Candidates & reads",
            "Matching is offline. Read execution admits only a positively matched built-in probe and exact local ProbeHost hash.",
            Labeled("Inventory JSON", inventoryPath),
            Labeled("Device ID", deviceId),
            Buttons(assess),
            Heading("Reviewed read-only probe"),
            Labeled("Probe ID", probeId),
            Labeled("Probe session output", probeOutput),
            Buttons(runProbe));
    }

    private TabItem BuildCaptureTab()
    {
        TextBox recipe = PathInput();
        TextBox output = PathInput(DefaultOutputDirectory());
        CheckBox scope = new()
        {
            Content = "I reviewed the observation scope; unknown observers remain unavailable",
            IsEnabled = false,
        };
        CheckBox exportReview = new()
        {
            Content = "I reviewed the actual redaction/quarantine preview below",
            IsEnabled = false,
        };
        Button review = new() { Content = "Review exact recipe scope" };
        Button prepare = new() { Content = "Prepare private observe-only capture" };
        Button export = new() { Content = "Export sanitized .wsgmcap", IsEnabled = false };
        recipe.TextChanged += (_, _) =>
        {
            _reviewedRecipeHash = null;
            scope.IsChecked = false;
            scope.IsEnabled = false;
        };
        review.Click += async (_, _) =>
        {
            _reviewedRecipeHash = null;
            string recipePath = recipe.Text!;
            await RunAsync(token => Task.Run<object?>(() =>
            {
                ObserveOnlyRecipeReview reviewed = _application.ReviewCaptureRecipe(recipePath);
                _reviewedRecipeHash = reviewed.RecipeSha256;
                return reviewed;
            }, token));
            scope.IsEnabled = _reviewedRecipeHash is not null;
        };
        prepare.Click += async (_, _) =>
        {
            _captureExportPlan = null;
            export.IsEnabled = false;
            exportReview.IsEnabled = false;
            exportReview.IsChecked = false;
            string recipePath = recipe.Text!;
            string outputPath = output.Text!;
            string reviewedHash = _reviewedRecipeHash ?? string.Empty;
            bool scopeConfirmed = scope.IsChecked is true;
            await RunAsync(async token =>
            {
                ObserveOnlyCaptureResult prepared = await Task.Run(() => _application.PrepareCaptureAsync(
                    new ObserveOnlyCaptureRequest
                    {
                        RecipePath = recipePath,
                        OutputDirectory = outputPath,
                        ReviewedRecipeSha256 = reviewedHash,
                        IsLocalInteractive = Environment.UserInteractive,
                        ObservationScopeConfirmed = scopeConfirmed,
                    },
                    DateTimeOffset.UtcNow,
                    token), token).ConfigureAwait(false);
                _captureExportPlan = prepared.ExportPlan;
                return prepared.ExportPlan is null
                    ? prepared
                    : new
                    {
                        prepared.Status,
                        prepared.ExportPlan.PrivateWorkingDirectory,
                        prepared.ExportPlan.ShareableOutputPath,
                        prepared.ExportPlan.Prompts,
                        prepared.ExportPlan.Redaction,
                        prepared.ExportPlan.Limitations,
                        shareableWritten = false,
                    };
            });
            bool ready = _captureExportPlan is not null;
            export.IsEnabled = ready;
            exportReview.IsEnabled = ready;
        };
        export.Click += async (_, _) =>
        {
            CaptureExportPlan? plan = _captureExportPlan;
            bool previewConfirmed = exportReview.IsChecked is true;
            await RunAsync(token => Task.Run<object?>(() =>
            {
                if (plan is null)
                {
                    throw new InvalidOperationException("Prepare a capture before exporting it.");
                }

                return _application.ExportCapture(plan, previewConfirmed);
            }, token));
        };
        return Tab(
            "Capture",
            "Preparation writes only the private session. Sanitized export is a separate approval after the actual privacy preview.",
            Labeled("Observe-only recipe", recipe),
            Labeled("Output root", output),
            Buttons(review),
            scope,
            Buttons(prepare),
            exportReview,
            Buttons(export));
    }

    private TabItem BuildWorkbenchTab()
    {
        TextBox left = PathInput();
        TextBox right = PathInput();
        TextBox action = new() { PlaceholderText = "Operator action ID" };
        TextBox sources = new() { PlaceholderText = "Comma-separated source IDs" };
        Button inspect = new() { Content = "Inspect capture A" };
        inspect.Click += async (_, _) =>
        {
            string capturePath = left.Text!;
            await RunAsync(token => Task.Run<object?>(() => _application.Inspect(capturePath), token));
        };
        Button diff = new() { Content = "Diff A ↔ B" };
        diff.Click += async (_, _) =>
        {
            string leftPath = left.Text!;
            string rightPath = right.Text!;
            await RunAsync(token => Task.Run<object?>(() => _application.Diff(leftPath, rightPath), token));
        };
        Button correlate = new() { Content = "Correlate action" };
        correlate.Click += async (_, _) =>
        {
            string capturePath = left.Text!;
            string actionId = action.Text!;
            HashSet<string> sourceIds = (sources.Text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
            await RunAsync(token => Task.Run<object?>(
                () => _application.Correlate(capturePath, actionId, sourceIds), token));
        };
        return Tab(
            "Capture workbench",
            "Every input bundle is hash-verified and bounded before inspection, comparison, or correlation.",
            Labeled("Capture A", left),
            Labeled("Capture B", right),
            Buttons(inspect, diff),
            Labeled("Action ID", action),
            Labeled("Expected sources", sources),
            Buttons(correlate));
    }

    private TabItem BuildScaffoldTab()
    {
        TextBox capture = PathInput();
        TextBox output = PathInput();
        TextBox publisher = new() { Text = "Unverified Device Lab contributor" };
        TextBox fixtureId = new() { PlaceholderText = "Stable fixture ID" };
        Button scaffold = new() { Content = "Generate read-only scaffold" };
        scaffold.Click += async (_, _) =>
        {
            string capturePath = capture.Text!;
            string outputPath = output.Text!;
            string publisherName = publisher.Text!;
            await RunAsync(token => Task.Run<object?>(
                () => _application.Scaffold(capturePath, outputPath, publisherName), token));
        };
        Button fixture = new() { Content = "Extract simulator-only fixture" };
        fixture.Click += async (_, _) =>
        {
            string capturePath = capture.Text!;
            string selectedFixture = fixtureId.Text!;
            string outputPath = output.Text!;
            await RunAsync(token => Task.Run<object?>(
                () => _application.ExtractFixture(capturePath, selectedFixture, outputPath), token));
        };
        return Tab(
            "Scaffold & fixture",
            "Generation uses exact evidence only. Output remains Scaffolded/Developer and gains no trust, privilege, or hardware verification.",
            Labeled("Verified capture", capture),
            Labeled("New output directory", output),
            Labeled("Publisher label", publisher),
            Labeled("Fixture ID", fixtureId),
            Buttons(scaffold, fixture));
    }

    private TabItem BuildPackageTab()
    {
        TextBox packageDirectory = PathInput();
        TextBox packageOutput = PathInput();
        TextBox glyphOutput = PathInput();
        Button validate = new() { Content = "Validate offline" };
        validate.Click += async (_, _) =>
        {
            string packagePath = packageDirectory.Text!;
            await RunAsync(token => Task.Run<object?>(() => _application.ValidateOffline(packagePath), token));
        };
        Button pack = new() { Content = "Validate and pack" };
        pack.Click += async (_, _) =>
        {
            string packagePath = packageDirectory.Text!;
            string outputPath = packageOutput.Text!;
            await RunAsync(token => Task.Run<object?>(() => _application.Pack(packagePath, outputPath), token));
        };
        Button generateGlyphs = new() { Content = "Import glyphs" };
        generateGlyphs.Click += async (_, _) =>
        {
            string packagePath = packageDirectory.Text!;
            string outputPath = glyphOutput.Text!;
            await RunAsync(token => Task.Run<object?>(
                () => _application.GenerateGlyphs(packagePath, outputPath), token));
        };
        return Tab(
            "Validate & pack",
            "Offline validation, glyph import, and packing grant no package trust, privilege, hardware verification, or retail support.",
            Labeled("Package directory", packageDirectory),
            Labeled("New .wsgmpkg path", packageOutput),
            Labeled("New glyph-generation directory", glyphOutput),
            Buttons(validate, generateGlyphs, pack));
    }

    private async Task RunAsync(Func<CancellationToken, Task<object?>> operation)
    {
        if (_operation is not null)
        {
            return;
        }

        _operation = new CancellationTokenSource();
        _cancel.IsEnabled = true;
        _result.Text = "Working…";
        try
        {
            object? result = await operation(_operation.Token);
            _result.Text = JsonSerializer.Serialize(result, DisplayJson);
        }
        catch (OperationCanceledException)
        {
            _result.Text = "Operation cancelled. No mutation authority was granted.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or ArgumentException or InvalidOperationException
            or NotSupportedException)
        {
            _result.Text = $"Operation failed: {exception.Message}";
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _cancel.IsEnabled = false;
        }
    }

    private void ApplyMode()
    {
        _tabs.ItemsSource = _mode.SelectedIndex == 1 ? _developerTabs : _ownerTabs;
        _tabs.SelectedIndex = 0;
    }

    private static TabItem Tab(string header, string description, params Control[] controls)
    {
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 11 };
        content.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Silver,
            Margin = new Thickness(0, 0, 0, 5),
        });
        foreach (Control control in controls)
        {
            content.Children.Add(control);
        }

        return new TabItem
        {
            Header = header,
            Content = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };
    }

    private static Control Labeled(string label, Control input)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("190,*"),
            ColumnSpacing = 10,
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(input, 1);
        row.Children.Add(input);
        return row;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (Button button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
    };

    private static TextBox PathInput(string? initial = null) => new()
    {
        Text = initial,
        PlaceholderText = "Absolute path",
    };

    private static string DefaultOutputDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "WSGM Device Lab");
}
