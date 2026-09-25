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
using Avalonia.Platform.Storage;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Scaffolding;
using WSGM.DeviceLab.Testing;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Gui;

internal sealed class MainWindow : Window
{
    private const int MaximumRecentPathsBytes = 64 * 1024;
    private const int MaximumRememberedPathCharacters = 4096;
    private const int MaximumRecentPathCount = 32;

    private static readonly JsonSerializerOptions DisplayJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly DeviceLabApplication _application;
    private readonly Button _cancel;
    private readonly IReadOnlyList<TabItem> _developerTabs;
    private readonly ComboBox _mode;
    private readonly TextBlock _operationStatus;
    private readonly IReadOnlyList<TabItem> _ownerTabs;
    private readonly Dictionary<string, string> _recentPaths = LoadRecentPaths();
    private readonly TextBox _result;
    private readonly TabControl _tabs;
    private CaptureExportPlan? _captureExportPlan;
    private bool _closeAfterOperation;
    private DeviceLabGuiOperationState _displayState = DeviceLabGuiOperationState.Initial;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource<bool>? _operationFinished;
    private string? _reviewedRecipeHash;

    public MainWindow()
    {
        var repositoryRoot = DeviceLabRepositoryLocator.Find(Environment.CurrentDirectory)
                             ?? DeviceLabRepositoryLocator.Find(AppContext.BaseDirectory);
        _application = new DeviceLabApplication(repositoryRoot, DeviceLabExecutable.CurrentPath);

        Title = "WSGM Device Lab";
        Width = 1180;
        Height = 800;
        MinWidth = 900;
        MinHeight = 620;
        Closing += HandleClosing;

        _mode = new ComboBox
        {
            ItemsSource = new[] { "Hardware Owner", "Plugin Developer" },
            SelectedIndex = 0,
            Width = 190
        };
        _mode.SelectionChanged += (_, _) => ApplyMode();
        _cancel = new Button { Content = "Cancel current operation", IsEnabled = false };
        _cancel.Click += (_, _) => _operation?.Cancel();

        var safety = BuildSafetyTab();
        var candidates = BuildCandidatesTab();
        var capture = BuildCaptureTab();
        var workbench = BuildWorkbenchTab();
        var scaffold = BuildScaffoldTab();
        var package = BuildPackageTab();
        var labReport = BuildLabReportTab(DeviceLabPathBoundaries.ForCurrentUser(repositoryRoot));
        _ownerTabs = [safety, candidates, capture, workbench];
        _developerTabs = [safety, candidates, capture, workbench, scaffold, package, labReport];
        _tabs = new TabControl { ItemsSource = _ownerTabs };

        _result = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 175,
            FontFamily = FontFamily.Default
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_result, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_result, ScrollBarVisibility.Auto);
        _operationStatus = new TextBlock
        {
            Text = _displayState.StatusText,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Silver
        };

        Grid root = new()
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,180"),
            RowSpacing = 10
        };
        root.Children.Add(Header());
        Grid.SetRow(_operationStatus, 1);
        root.Children.Add(_operationStatus);
        Grid.SetRow(_tabs, 2);
        root.Children.Add(_tabs);
        var resultHeading = Heading("Result / preview");
        Grid.SetRow(resultHeading, 3);
        root.Children.Add(resultHeading);
        Grid.SetRow(_result, 4);
        root.Children.Add(_result);
        Content = root;
    }

    private Grid Header()
    {
        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 12
        };
        StackPanel title = new() { Spacing = 3 };
        title.Children.Add(new TextBlock
        {
            Text = "WSGM Device Lab",
            FontSize = 25,
            FontWeight = FontWeight.SemiBold
        });
        title.Children.Add(new TextBlock
        {
            Text = "Read-only by default. Only the attended local plugin action may touch hardware.",
            Foreground = Brushes.Silver
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
        var output = PathInput("inventory-output", PathSelectionKind.Folder, DefaultOutputDirectory());
        CheckBox shareable = new()
        {
            Content = "Create a shareable inventory (redact unique identifiers)",
            IsChecked = true
        };
        Button doctor = new() { Content = "Run doctor" };
        doctor.Click += async (_, _) =>
        {
            var outputPath = output.Text!;
            await RunAsync(token => Task.Run<object?>(
                () => _application.Doctor(outputPath, DateTimeOffset.UtcNow, token), token));
        };
        Button inventory = new() { Content = "Collect inventory" };
        inventory.Click += async (_, _) =>
        {
            var outputPath = output.Text!;
            var sanitize = shareable.IsChecked is true;
            await RunAsync(token => Task.Run<object?>(() => _application.Inventory(
                outputPath,
                sanitize,
                DateTimeOffset.UtcNow,
                token), token));
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
        var inventoryPath = PathInput("inventory-file", PathSelectionKind.OpenFile);
        TextBox deviceId = new() { PlaceholderText = "Optional exact logical device ID" };
        TextBox probeId = new() { PlaceholderText = "Reviewed probe ID from candidate output" };
        var probeOutput = PathInput("probe-output", PathSelectionKind.Folder, DefaultOutputDirectory());
        Button assess = new() { Content = "Compare candidates and read probes" };
        assess.Click += async (_, _) =>
        {
            var inventoryFile = inventoryPath.Text!;
            var targetDevice = string.IsNullOrWhiteSpace(deviceId.Text) ? null : deviceId.Text;
            await RunAsync(token => Task.Run<object?>(
                () => DeviceLabApplication.Candidates(inventoryFile, targetDevice, token), token));
        };
        Button runProbe = new() { Content = "Run selected reviewed read probe" };
        runProbe.Click += async (_, _) =>
        {
            var inventoryFile = inventoryPath.Text!;
            var selectedProbe = probeId.Text!;
            var outputPath = probeOutput.Text!;
            await RunAsync(token => Task.Run<object?>(async () => await _application.RunReadProbeAsync(
                inventoryFile,
                selectedProbe,
                outputPath,
                token).ConfigureAwait(false), token));
        };
        return Tab(
            "Candidates & reads",
            "Matching is offline. Read execution requires an exact known-device match and uses the disposable Device Lab self-worker.",
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
        var recipe = PathInput("capture-recipe", PathSelectionKind.OpenFile);
        var output = PathInput("capture-output", PathSelectionKind.Folder, DefaultOutputDirectory());
        CheckBox scope = new()
        {
            Content = "I reviewed the observation scope; unknown observers remain unavailable",
            IsEnabled = false
        };
        CheckBox exportReview = new()
        {
            Content = "I reviewed the bounded preview of every sanitized shareable-content lane below",
            IsEnabled = false
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
            var recipePath = recipe.Text!;
            await RunAsync(
                token => Task.Run<object?>(
                    () => DeviceLabApplication.ReviewCaptureRecipe(recipePath, token),
                    token),
                accepted =>
                {
                    var reviewed = (ObserveOnlyRecipeReview)accepted!;
                    _reviewedRecipeHash = reviewed.RecipeSha256;
                    scope.IsEnabled = true;
                });
        };
        prepare.Click += async (_, _) =>
        {
            _captureExportPlan = null;
            export.IsEnabled = false;
            exportReview.IsEnabled = false;
            exportReview.IsChecked = false;
            var recipePath = recipe.Text!;
            var outputPath = output.Text!;
            var reviewedHash = _reviewedRecipeHash ?? string.Empty;
            var scopeConfirmed = scope.IsChecked is true;
            await RunAsync(async token =>
                {
                    var prepared = await Task.Run(() => _application.PrepareCaptureAsync(
                        new ObserveOnlyCaptureRequest
                        {
                            RecipePath = recipePath,
                            OutputDirectory = outputPath,
                            ReviewedRecipeSha256 = reviewedHash,
                            IsLocalInteractive = Environment.UserInteractive,
                            ObservationScopeConfirmed = scopeConfirmed
                        },
                        DateTimeOffset.UtcNow,
                        token), token).ConfigureAwait(false);
                    object display = prepared.ExportPlan is null
                        ? prepared
                        : new
                        {
                            prepared.Status,
                            prepared.ExportPlan.PrivateWorkingDirectory,
                            prepared.ExportPlan.ShareableOutputPath,
                            prepared.ExportPlan.Prompts,
                            privacyPreview = CapturePrivacyPreview.Create(prepared.ExportPlan.Bundle),
                            prepared.ExportPlan.Limitations,
                            shareableWritten = false
                        };
                    return new PreparedCaptureOperation(prepared.ExportPlan, display);
                },
                accepted =>
                {
                    var prepared = (PreparedCaptureOperation)accepted!;
                    _captureExportPlan = prepared.ExportPlan;
                    var ready = _captureExportPlan is not null;
                    export.IsEnabled = ready;
                    exportReview.IsEnabled = ready;
                },
                display => ((PreparedCaptureOperation)display!).Display);
        };
        export.Click += async (_, _) =>
        {
            var plan = _captureExportPlan;
            var previewConfirmed = exportReview.IsChecked is true;
            await RunAsync(token => Task.Run<object?>(() => plan is null
                ? throw new InvalidOperationException("Prepare a capture before exporting it.")
                : _application.ExportCapture(plan, previewConfirmed, token), token));
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
        var left = PathInput("capture-a", PathSelectionKind.OpenFile);
        var right = PathInput("capture-b", PathSelectionKind.OpenFile);
        TextBox action = new() { PlaceholderText = "Operator action ID" };
        TextBox sources = new() { PlaceholderText = "Comma-separated source IDs" };
        Button inspect = new() { Content = "Inspect capture A" };
        inspect.Click += async (_, _) =>
        {
            var capturePath = left.Text!;
            await RunAsync(token => Task.Run<object?>(() => DeviceLabApplication.Inspect(capturePath, token), token));
        };
        Button diff = new() { Content = "Diff A ↔ B" };
        diff.Click += async (_, _) =>
        {
            var leftPath = left.Text!;
            var rightPath = right.Text!;
            await RunAsync(token =>
                Task.Run<object?>(() => DeviceLabApplication.Diff(leftPath, rightPath, token), token));
        };
        Button correlate = new() { Content = "Correlate action" };
        correlate.Click += async (_, _) =>
        {
            var capturePath = left.Text!;
            var actionId = action.Text!;
            var sourceIds = (sources.Text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
            await RunAsync(token => Task.Run<object?>(
                () => DeviceLabApplication.Correlate(capturePath, actionId, sourceIds, token), token));
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
        var capture = PathInput("scaffold-capture", PathSelectionKind.OpenFile);
        var output = PathInput(
            "scaffold-output",
            PathSelectionKind.NewFolder,
            suggestedName: "new-device-plugin");
        TextBox usbInstance = new()
        {
            PlaceholderText = "Required when the capture contains multiple exact USB endpoints"
        };
        TextBox fixtureId = new() { PlaceholderText = "Stable fixture ID" };
        Button scaffold = new() { Content = "Copy minimal plugin template" };
        scaffold.Click += async (_, _) =>
        {
            var capturePath = capture.Text!;
            var outputPath = output.Text!;
            await RunAsync(token => Task.Run<object?>(
                () => _application.Scaffold(
                    capturePath,
                    outputPath,
                    string.IsNullOrWhiteSpace(usbInstance.Text) ? null : usbInstance.Text,
                    token), token));
        };
        Button fixture = new() { Content = "Extract simulator-only fixture" };
        fixture.Click += async (_, _) =>
        {
            var capturePath = capture.Text!;
            var selectedFixture = fixtureId.Text!;
            var outputPath = output.Text!;
            await RunAsync(token => Task.Run<object?>(
                () => _application.ExtractFixture(capturePath, selectedFixture, outputPath, token), token));
        };
        return Tab(
            "Scaffold & fixture",
            "Device Lab copies the checked-in minimal template and replaces only exact captured identity tokens.",
            Labeled("Verified capture", capture),
            Labeled("New output directory", output),
            Labeled("USB instance ID", usbInstance),
            Labeled("Fixture ID", fixtureId),
            Buttons(scaffold, fixture));
    }

    // A returned .wsgmlab report: review it against the knowledge base, promote the confirmed fields to a
    // new record file, or scaffold a plugin project from it. The same services as the CLI commands.
    private TabItem BuildLabReportTab(DeviceLabPathBoundaries boundaries)
    {
        var report = PathInput("lab-report", PathSelectionKind.OpenFile);
        var record = PathInput("lab-record", PathSelectionKind.SaveFile);
        var project = PathInput("lab-scaffold", PathSelectionKind.NewFolder, suggestedName: "new-device-plugin");
        TextBox fields = new()
            { PlaceholderText = "Field IDs to promote, comma-separated; empty promotes every confirmation" };
        Button review = new() { Content = "Review against the knowledge base" };
        review.Click += async (_, _) =>
        {
            var reportPath = report.Text!;
            await RunAsync(token => Task.Run<object?>(() => LabReview.Review(reportPath), token));
        };
        Button promote = new() { Content = "Write promoted record" };
        promote.Click += async (_, _) =>
        {
            var reportPath = report.Text!;
            var recordPath = record.Text!;
            string[] selected =
            [
                .. (fields.Text ?? string.Empty).Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ];
            await RunAsync(token => Task.Run<object?>(
                () => LabPromote.Run(reportPath, recordPath, selected, boundaries), token));
        };
        Button scaffold = new() { Content = "Scaffold plugin from report" };
        scaffold.Click += async (_, _) =>
        {
            var reportPath = report.Text!;
            var outputPath = project.Text!;
            await RunAsync(token => Task.Run<object?>(
                () => ScaffoldFromLabProjectWorkflow.Run(reportPath, outputPath, boundaries,
                    cancellationToken: token), token));
        };
        return Tab(
            "Lab report",
            "Reads a returned .wsgmlab report without extracting it, lists where it agrees and disagrees with the knowledge record, and writes new files only.",
            Labeled("Lab report (.wsgmlab)", report),
            Buttons(review),
            Labeled("New record file", record),
            Labeled("Fields", fields),
            Buttons(promote),
            Labeled("New plugin directory", project),
            Buttons(scaffold));
    }

    private TabItem BuildPackageTab()
    {
        var packageDirectory = PathInput("plugin-package", PathSelectionKind.Folder);
        var packageOutput = PathInput("plugin-package-output", PathSelectionKind.SaveFile);
        var inventory = PathInput("plugin-inventory", PathSelectionKind.OpenFile);
        var stateDirectory = PathInput(
            "plugin-state",
            PathSelectionKind.NewFolder,
            suggestedName: "new-plugin-state");
        ComboBox hardwareAction = new()
        {
            ItemsSource = new[] { "Capability value", "Haptic pulse", "Controller management" },
            SelectedIndex = 0
        };
        TextBox capabilityId = new() { PlaceholderText = "For example power.sustained-limit" };
        TextBox capabilityInstance = new() { PlaceholderText = "Optional exact instance ID" };
        TextBox capabilityValue = new()
        {
            PlaceholderText = "true | 24 | choice | #RRGGBB | 40:20,70:60 | plain text"
        };
        hardwareAction.SelectionChanged += (_, _) => ApplyHardwareActionSelection();
        ApplyHardwareActionSelection();
        Button validate = new() { Content = "Validate offline" };
        validate.Click += async (_, _) =>
        {
            var packagePath = packageDirectory.Text!;
            await RunAsync(token =>
                Task.Run<object?>(() => DeviceLabApplication.ValidateOffline(packagePath, token), token));
        };
        Button pack = new() { Content = "Validate and pack" };
        pack.Click += async (_, _) =>
        {
            var packagePath = packageDirectory.Text!;
            var outputPath = packageOutput.Text!;
            await RunAsync(token => Task.Run<object?>(() => _application.Pack(packagePath, outputPath, token), token));
        };
        Button generateGlyphs = new() { Content = "Import glyphs" };
        generateGlyphs.Click += async (_, _) =>
        {
            var packagePath = packageDirectory.Text!;
            await RunAsync(token =>
                Task.Run<object?>(() => DeviceLabApplication.ImportGlyphs(packagePath, token), token));
        };
        Button testSample = new() { Content = "Test synthetic sample" };
        testSample.Click += async (_, _) =>
        {
            await RunAsync(async token =>
                await DeviceLabApplication.TestSyntheticPluginAsync(token).ConfigureAwait(false));
        };
        Button testPlugin = new() { Content = "Test plugin detection" };
        testPlugin.Click += async (_, _) =>
        {
            var packagePath = packageDirectory.Text!;
            var inventoryPath = inventory.Text!;
            await RunAsync(async token => await DeviceLabApplication.TestPluginAsync(
                packagePath,
                inventoryPath,
                token).ConfigureAwait(false));
        };
        Button runHardware = new() { Content = "Run attended hardware action" };
        runHardware.Click += async (_, _) =>
        {
            var action = hardwareAction.SelectedIndex switch
            {
                1 => new AttendedPluginActionRequest
                {
                    Kind = AttendedPluginActionKind.HapticPulse,
                    InstanceId = string.IsNullOrWhiteSpace(capabilityInstance.Text)
                        ? null
                        : capabilityInstance.Text
                },
                2 => new AttendedPluginActionRequest
                {
                    Kind = AttendedPluginActionKind.ControllerManagement,
                    InstanceId = string.IsNullOrWhiteSpace(capabilityInstance.Text)
                        ? null
                        : capabilityInstance.Text
                },
                _ => new AttendedPluginActionRequest
                {
                    Kind = AttendedPluginActionKind.CapabilityValue,
                    CapabilityId = capabilityId.Text,
                    InstanceId = string.IsNullOrWhiteSpace(capabilityInstance.Text)
                        ? null
                        : capabilityInstance.Text,
                    ValueText = capabilityValue.Text
                }
            };
            if (action.Kind is AttendedPluginActionKind.CapabilityValue
                && (string.IsNullOrWhiteSpace(action.CapabilityId)
                    || string.IsNullOrWhiteSpace(action.ValueText)))
            {
                ApplyDisplayState(_displayState.Failed(
                    "Capability value actions require an exact capability ID and value before confirmation."));
                return;
            }

            if (_operation is not null || !await ConfirmHardwareActionAsync(action))
            {
                return;
            }

            var packagePath = packageDirectory.Text!;
            var inventoryPath = inventory.Text!;
            var statePath = stateDirectory.Text!;
            await RunAsync(async token => await _application.RunAttendedPluginAsync(
                packagePath,
                inventoryPath,
                statePath,
                action,
                true,
                token).ConfigureAwait(false));
        };
        return Tab(
            "Test, validate & pack",
            "Validation, synthetic testing, local detection, glyph import, packing, and the one explicit attended hardware action.",
            Labeled("Package directory", packageDirectory),
            Labeled("Current inventory JSON", inventory),
            Labeled("New .wsgmpkg path", packageOutput),
            Labeled("New plugin state directory", stateDirectory),
            Labeled("Attended action", hardwareAction),
            Labeled("Capability ID (capability value only)", capabilityId),
            Labeled("Exact capability/controller instance", capabilityInstance),
            Labeled("Capability value (capability value only)", capabilityValue),
            Buttons(validate, testSample, testPlugin),
            Buttons(generateGlyphs, pack),
            Buttons(runHardware));

        void ApplyHardwareActionSelection()
        {
            var capabilitySelected = hardwareAction.SelectedIndex == 0;
            capabilityId.IsEnabled = capabilitySelected;
            capabilityInstance.IsEnabled = true;
            capabilityValue.IsEnabled = capabilitySelected;
        }
    }

    private async Task<bool> ConfirmHardwareActionAsync(AttendedPluginActionRequest action)
    {
        TextBox confirmation = new() { PlaceholderText = "Type RUN HARDWARE" };
        Button run = new() { Content = "Run once" };
        Button cancel = new() { Content = "Cancel" };
        Window dialog = new()
        {
            Title = "Confirm attended hardware action",
            Width = 520,
            Height = 260,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text =
                            $"Selected action: {DescribeHardwareAction(action)}. This loads the selected plugin on the exact target and may access or change hardware. Device Integration must be stopped. Type RUN HARDWARE for this run only.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    confirmation,
                    Buttons(cancel, run)
                }
            }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        run.Click += (_, _) => dialog.Close(string.Equals(
            confirmation.Text,
            "RUN HARDWARE",
            StringComparison.Ordinal));
        return await dialog.ShowDialog<bool>(this);
    }

    private static string DescribeHardwareAction(AttendedPluginActionRequest action)
    {
        return action.Kind switch
        {
            AttendedPluginActionKind.CapabilityValue => action.InstanceId is null
                ? $"set {action.CapabilityId} to {action.ValueText}, verify it, and restore its original value"
                : $"set {action.CapabilityId}/{action.InstanceId} to {action.ValueText}, verify it, and restore its original value",
            AttendedPluginActionKind.HapticPulse =>
                action.InstanceId is null
                    ? "send one fixed 250 ms haptic pulse, stop output, and restore controller topology"
                    : $"send one fixed 250 ms haptic pulse to {action.InstanceId}, stop output, and restore controller topology",
            AttendedPluginActionKind.ControllerManagement =>
                action.InstanceId is null
                    ? "acquire controller management once and restore its verified topology"
                    : $"acquire controller instance {action.InstanceId} once and restore its verified topology",
            _ => action.Kind.ToString()
        };
    }

    private async Task RunAsync(
        Func<CancellationToken, Task<object?>> operation,
        Action<object?>? accepted = null,
        Func<object?, object?>? display = null)
    {
        if (_operation is not null)
        {
            _operationStatus.Text = "An operation is already running. Cancel it or wait for completion.";
            return;
        }

        CancellationTokenSource current = new();
        TaskCompletionSource<bool> finished = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = current;
        _operationFinished = finished;
        _cancel.IsEnabled = true;
        _tabs.IsEnabled = false;
        _mode.IsEnabled = false;
        ApplyDisplayState(_displayState.Started());
        try
        {
            // Async workflows can validate packages, load plugins, or enumerate the machine before
            // their first await. Start every workflow on a worker so that synchronous prefix never
            // stalls Avalonia's UI thread.
            var result = await Task.Run(() => operation(current.Token), current.Token);
            current.Token.ThrowIfCancellationRequested();
            var serialized = JsonSerializer.Serialize(display?.Invoke(result) ?? result, DisplayJson);
            accepted?.Invoke(result);
            ApplyDisplayState(DeviceLabGuiOperationState.Succeeded(serialized));
        }
        catch (OperationCanceledException)
        {
            ApplyDisplayState(_displayState.Cancelled());
        }
        catch (Exception exception)
        {
            ApplyDisplayState(_displayState.Failed(OperationFailureMessage(exception)));
        }
        finally
        {
            current.Dispose();
            if (ReferenceEquals(_operation, current))
            {
                _operation = null;
            }

            if (ReferenceEquals(_operationFinished, finished))
            {
                _operationFinished = null;
            }

            finished.TrySetResult(true);
            _cancel.IsEnabled = false;
            _tabs.IsEnabled = true;
            _mode.IsEnabled = true;
        }
    }

    private void ApplyDisplayState(DeviceLabGuiOperationState state)
    {
        _displayState = state;
        _operationStatus.Text = state.StatusText;
        _result.Text = state.LastSuccessfulResult ?? string.Empty;
    }

    private static string OperationFailureMessage(Exception exception)
    {
        const int maximumCharacters = 1024;
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return message.Length <= maximumCharacters ? message : message[..maximumCharacters];
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod
    private async void HandleClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_closeAfterOperation || _operation is not { } operation
                                 || _operationFinished is not { } finished)
        {
            return;
        }

        eventArgs.Cancel = true;
        try
        {
            await operation.CancelAsync();
            await finished.Task;
            _closeAfterOperation = true;
            Close();
        }
        catch (Exception exception)
        {
            ApplyDisplayState(_displayState.Failed(OperationFailureMessage(exception)));
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
            Margin = new Thickness(0, 0, 0, 5)
        });
        foreach (var control in controls)
        {
            content.Children.Add(control);
        }

        return new TabItem
        {
            Header = header,
            Content = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
    }

    private Grid Labeled(string label, Control input)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("190,*"),
            ColumnSpacing = 10
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        });
        var renderedInput = input is PathTextBox pathInput
            ? PathPicker(pathInput)
            : input;
        Grid.SetColumn(renderedInput, 1);
        row.Children.Add(renderedInput);
        return row;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        foreach (var button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }

    private static TextBlock Heading(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold
        };
    }

    private PathTextBox PathInput(
        string recentKey,
        PathSelectionKind selectionKind,
        string? initial = null,
        string? suggestedName = null)
    {
        var remembered = _recentPaths.GetValueOrDefault(recentKey);
        var value = remembered ?? initial;
        if (selectionKind is PathSelectionKind.NewFolder && remembered is not null)
        {
            try
            {
                value = NextAvailableDirectory(remembered, suggestedName!);
            }
            catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or ArgumentException
                                                  or NotSupportedException)
            {
                value = initial;
            }
        }

        var input = new PathTextBox
        {
            RecentKey = recentKey,
            SelectionKind = selectionKind,
            SuggestedName = suggestedName,
            Text = value,
            PlaceholderText = "Absolute path"
        };
        input.LostFocus += (_, _) => RememberPath(input);
        return input;
    }

    private Grid PathPicker(PathTextBox input)
    {
        Grid picker = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8
        };
        picker.Children.Add(input);
        Button browse = new() { Content = "Browse…", MinWidth = 88 };
        browse.Click += async (_, _) => await BrowseForPathAsync(input);
        Grid.SetColumn(browse, 1);
        picker.Children.Add(browse);
        return picker;
    }

    private async Task BrowseForPathAsync(PathTextBox input)
    {
        try
        {
            string? selected;
            switch (input.SelectionKind)
            {
                case PathSelectionKind.Folder:
                case PathSelectionKind.NewFolder:
                    var folders = await StorageProvider.OpenFolderPickerAsync(
                        new FolderPickerOpenOptions
                        {
                            Title = "Select folder",
                            AllowMultiple = false
                        });
                    selected = folders.Count == 0 ? null : folders[0].Path.LocalPath;
                    if (selected is not null && input.SelectionKind is PathSelectionKind.NewFolder)
                    {
                        selected = NextAvailableDirectory(selected, input.SuggestedName!);
                    }

                    break;
                case PathSelectionKind.SaveFile:
                    var saved = await StorageProvider.SaveFilePickerAsync(
                        new FilePickerSaveOptions
                        {
                            Title = "Select new file",
                            SuggestedFileName = string.IsNullOrWhiteSpace(input.Text)
                                ? null
                                : Path.GetFileName(input.Text)
                        });
                    selected = saved?.Path.LocalPath;
                    break;
                case PathSelectionKind.OpenFile:
                default:
                    var files = await StorageProvider.OpenFilePickerAsync(
                        new FilePickerOpenOptions
                        {
                            Title = "Select file",
                            AllowMultiple = false
                        });
                    selected = files.Count == 0 ? null : files[0].Path.LocalPath;
                    break;
            }

            if (!string.IsNullOrWhiteSpace(selected))
            {
                input.Text = selected;
                RememberPath(input);
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            ApplyDisplayState(_displayState.Failed(OperationFailureMessage(exception)));
        }
    }

    private void RememberPath(PathTextBox input)
    {
        if (string.IsNullOrWhiteSpace(input.Text)
            || input.Text.Length > MaximumRememberedPathCharacters)
        {
            return;
        }

        string remembered;
        try
        {
            remembered = input.SelectionKind is PathSelectionKind.NewFolder
                ? Path.GetDirectoryName(Path.GetFullPath(input.Text)) ?? input.Text
                : input.Text;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            return;
        }

        _recentPaths[input.RecentKey] = remembered;
        SaveRecentPaths(_recentPaths);
    }

    private static string NextAvailableDirectory(string parent, string suggestedName)
    {
        var candidate = Path.Combine(parent, suggestedName);
        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
        {
            if (suffix > 100)
            {
                return Path.Combine(parent, $"{suggestedName}-{Guid.NewGuid():N}");
            }

            candidate = Path.Combine(parent, $"{suggestedName}-{suffix}");
        }

        return candidate;
    }

    private static Dictionary<string, string> LoadRecentPaths()
    {
        try
        {
            var path = RecentPathsFile();
            FileInfo file = new(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumRecentPathsBytes)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllBytes(path));
            return loaded is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : loaded
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                                   && pair.Key.Length <= 128
                                   && !string.IsNullOrWhiteSpace(pair.Value)
                                   && pair.Value.Length <= MaximumRememberedPathCharacters)
                    .Take(MaximumRecentPathCount)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void SaveRecentPaths(IReadOnlyDictionary<string, string> paths)
    {
        string? temporary = null;
        try
        {
            var path = RecentPathsFile();
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $"recent-paths.{Guid.NewGuid():N}.tmp");
            var bounded = paths
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                               && pair.Key.Length <= 128
                               && !string.IsNullOrWhiteSpace(pair.Value)
                               && pair.Value.Length <= MaximumRememberedPathCharacters)
                .Take(MaximumRecentPathCount)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var json = JsonSerializer.SerializeToUtf8Bytes(bounded);
            while (json.Length > MaximumRecentPathsBytes && bounded.Count > 0)
            {
                var longest = bounded.MaxBy(pair => pair.Value.Length);
                bounded.Remove(longest.Key);
                json = JsonSerializer.SerializeToUtf8Bytes(bounded);
            }

            File.WriteAllBytes(temporary, json);
            File.Move(temporary, path, true);
            temporary = null;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            // Path history is a convenience only; workflow results remain authoritative.
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException
                                                      or UnauthorizedAccessException)
                {
                    // Best-effort cleanup of a uniquely named local preferences temp file.
                }
            }
        }
    }

    private static string RecentPathsFile()
    {
        return Path.Combine(
            // wsgm-allow-live-data-path: Device Lab's own root beside WSGM's data, never inside it.
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSGM Device Lab",
            "recent-paths.json");
    }

    private static string DefaultOutputDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WSGM Device Lab");
    }

    private sealed record PreparedCaptureOperation(
        CaptureExportPlan? ExportPlan,
        object Display);

    private enum PathSelectionKind
    {
        OpenFile,
        SaveFile,
        Folder,
        NewFolder
    }

    private sealed class PathTextBox : TextBox
    {
        internal required string RecentKey { get; init; }

        internal required PathSelectionKind SelectionKind { get; init; }

        internal string? SuggestedName { get; init; }
    }
}
