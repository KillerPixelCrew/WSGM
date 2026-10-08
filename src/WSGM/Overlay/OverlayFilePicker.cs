using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>A two-pane local browser inside the overlay's existing modal and input lifetime.</summary>
internal sealed class OverlayFilePicker : UserControl
{
    private readonly ActionButton _choose = new() { Title = "Choose this folder", IsEnabled = false };
    private readonly string[] _extensions;
    private readonly bool _folder;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ActionButton _location = new() { Title = "Enter a path", IconGeometry = Icons.FolderPlus };
    private readonly ActionButton _open = new() { Title = "Open", IsEnabled = false };
    private readonly StackPanel _places = new() { Spacing = 6 };
    private readonly Func<string, IReadOnlyCollection<string>, CancellationToken, Task<SteamFileListing>> _readFolder;
    private readonly Func<CancellationToken, Task<SteamFilePlaces>> _readPlaces;

    private readonly TextBlock _selection = new()
        { Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis };

    private readonly TextBlock _status = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private readonly ActionButton _up = new() { Title = "Up", IconGeometry = Icons.ArrowLeft, IsEnabled = false };
    private bool _busy;
    private bool _completed;
    private bool _focusPending;
    private long _generation;
    private SteamFileListing? _listing;
    private CancellationTokenSource? _load;

    internal OverlayFilePicker(bool folder, IEnumerable<string> extensions,
        Func<CancellationToken, Task<SteamFilePlaces>>? readPlaces = null,
        Func<string, IReadOnlyCollection<string>, CancellationToken, Task<SteamFileListing>>? readFolder = null)
    {
        _folder = folder;
        _extensions = extensions.ToArray();
        _readPlaces = readPlaces ?? (token => Task.Run(SteamFilePickerSurface.ListPlaces, token).WaitAsync(token));
        _readFolder = readFolder ?? ((path, filters, token) =>
            Task.Run(() => SteamFilePickerSurface.ListFolder(path, filters, token), token).WaitAsync(token));
        _choose.IsVisible = folder;
        _choose.Classes.Add("primary");
        _open.Classes.Set("primary", !folder);
        _location.Click += (_, _) =>
            TextEntryRequested?.Invoke(_listing?.Path ?? "", value => _ = EnterPathAsync(value));
        _up.Click += (_, _) =>
        {
            if (_listing?.Parent is { Length: > 0 } parent)
            {
                OpenDirectory(parent);
            }
        };
        _open.Click += (_, _) => OpenSelection();
        _choose.Click += (_, _) =>
        {
            if (!_busy && _listing is { Error: null })
            {
                Finish(_listing.Path);
            }
        };
        Entries.ItemTemplate = new FuncDataTemplate<SteamFileEntry>((entry, _) =>
        {
            var row = new Grid
                { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, MinHeight = 36 };
            var name = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(SteamFileEntry.Name)));
            row.Children.Add(name);
            var kind = new TextBlock
            {
                Classes = { "caption" },
                VerticalAlignment = VerticalAlignment.Center
            };
            kind.Bind(TextBlock.TextProperty, new Binding(".")
            {
                Converter = new FuncValueConverter<SteamFileEntry, string>(item =>
                    item is null ? "" : item.Folder ? "Folder" : Path.GetExtension(item.Name))
            });
            Grid.SetColumn(kind, 1);
            row.Children.Add(kind);
            var border = new Border
            {
                Child = row, Padding = new Thickness(8, 4),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("DeckDividerBrush"));
            return border;
        }, true);
        Entries.SelectionChanged += (_, _) => UpdateActions();
        Entries.GotFocus += (_, change) =>
        {
            if (change.Source is ListBoxItem { DataContext: SteamFileEntry entry })
            {
                Entries.SelectedItem = entry;
            }
        };
        Entries.DoubleTapped += (_, _) => OpenSelection();
        Entries.AddHandler(KeyDownEvent, (_, key) =>
        {
            if (key.Key is Key.Enter or Key.Space)
            {
                key.Handled = true;
                OpenSelection();
            }
        }, RoutingStrategies.Tunnel);
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        _up.MinWidth = 76;
        toolbar.Children.Add(_up);
        Grid.SetColumn(_location, 1);
        toolbar.Children.Add(_location);
        var browser = new Grid { ColumnDefinitions = new ColumnDefinitions("1*,2.4*"), ColumnSpacing = 12 };
        var placesPane = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(4) };
        placesPane.Bind(Border.BackgroundProperty, placesPane.GetResourceObservable("DeckGroupBrush"));
        var placeBody = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        placeBody.Children.Add(new TextBlock
            { Text = "Places", Classes = { "eyebrow" }, Margin = new Thickness(8, 4) });
        var placesScroller = new ScrollViewer
        {
            Content = _places, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(placesScroller, 1);
        placeBody.Children.Add(placesScroller);
        placesPane.Child = placeBody;
        browser.Children.Add(placesPane);
        var filesPane = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(4) };
        filesPane.Bind(Border.BackgroundProperty, filesPane.GetResourceObservable("DeckGroupBrush"));
        var fileBody = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 6 };
        var headings = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 4) };
        headings.Children.Add(new TextBlock { Text = "Name", Classes = { "eyebrow" } });
        var typeHeading = new TextBlock { Text = "Type", Classes = { "eyebrow" } };
        Grid.SetColumn(typeHeading, 1);
        headings.Children.Add(typeHeading);
        fileBody.Children.Add(headings);
        Grid.SetRow(Entries, 1);
        fileBody.Children.Add(Entries);
        filesPane.Child = fileBody;
        Grid.SetColumn(filesPane, 1);
        browser.Children.Add(filesPane);
        var cancel = new ActionButton { Title = "Cancel" };
        cancel.Click += (_, _) => Finish(null);
        var actions = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var action in new[] { cancel, _open, _choose })
        {
            action.MinWidth = 100;
            action.HorizontalAlignment = HorizontalAlignment.Left;
            actions.Children.Add(action);
        }

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        _selection.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_selection);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 10 };
        root.Children.Add(toolbar);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);
        Grid.SetRow(browser, 2);
        root.Children.Add(browser);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;
        AttachedToVisualTree += (_, _) => Initialization = InitializeAsync();
        DetachedFromVisualTree += (_, _) =>
        {
            _generation++;
            _lifetime.Cancel();
            _load?.Cancel();
            _load?.Dispose();
            _load = null;
            Finish(null);
        };
    }

    internal InputElement DefaultFocusTarget => Entries.SelectedIndex >= 0
        ? Entries.ContainerFromIndex(Entries.SelectedIndex) ?? (InputElement)Entries
        : _location;

    internal Task Initialization { get; private set; } = Task.CompletedTask;
    internal ListBox Entries { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    internal event Action<string?>? Completed;
    internal event Action<string, Action<string>>? TextEntryRequested;
    internal event Action<SteamFileListing>? DirectoryLoaded;

    internal bool RestorePendingFocus()
    {
        if (!_focusPending || _completed || !IsEffectivelyVisible)
        {
            return false;
        }

        UpdateLayout();
        if (!DefaultFocusTarget.Focus(NavigationMethod.Directional))
        {
            return false;
        }

        _focusPending = false;
        return true;
    }

    internal bool Navigate(NavigationDirection direction)
    {
        if (!Entries.IsKeyboardFocusWithin || Entries.ItemCount == 0
                                           || direction is not (NavigationDirection.Up or NavigationDirection.Down))
        {
            return false;
        }

        var index = Entries.SelectedIndex + (direction == NavigationDirection.Down ? 1 : -1);
        if (index < 0 || index >= Entries.ItemCount)
        {
            return false;
        }

        Entries.SelectedIndex = index;
        Entries.ScrollIntoView(Entries.SelectedItem!);
        Entries.UpdateLayout();
        DefaultFocusTarget.Focus(NavigationMethod.Directional);
        return true;
    }

    private async Task InitializeAsync()
    {
        try
        {
            _status.Text = "Reading places…";
            var places = await _readPlaces(_lifetime.Token);
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            foreach (var place in places.Places)
            {
                var button = new ActionButton { Title = place.Name, Description = place.Detail, Tag = place.Path };
                button.Click += (_, _) => OpenDirectory(place.Path);
                _places.Children.Add(button);
            }

            if (_generation == 0 && places.Places.FirstOrDefault() is { } first)
            {
                await ReadDirectoryAsync(first.Path);
            }
            else if (_generation == 0)
            {
                _status.Text = "No places are available. Enter a path to browse.";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
        }
    }

    private void OpenDirectory(string path)
    {
        _ = ReadDirectoryAsync(path);
    }

    private async Task ReadDirectoryAsync(string path)
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _load.Token;
        var generation = ++_generation;
        _busy = true;
        _focusPending = false;
        _status.Text = "Reading " + path + "…";
        Entries.IsEnabled = false;
        UpdateActions();
        try
        {
            var listing = await _readFolder(path, _folder ? [] : _extensions.Length > 0 ? _extensions : [".*"], token);
            if (generation != _generation || token.IsCancellationRequested || _completed)
            {
                return;
            }

            _listing = listing;
            _busy = false;
            Entries.IsEnabled = true;
            Entries.ItemsSource = listing.Entries;
            Entries.SelectedIndex = listing.Entries.Count > 0 ? 0 : -1;
            _location.Title = listing.Path;
            _location.Description = "Enter a path";
            _status.Text = listing.Error ?? (listing.Entries.Count == 0
                ? "This folder is empty."
                : listing.Entries.Count + " items" + (_folder ? " · Choose a folder" : " · Select a file"));
            _up.IsEnabled = listing.Parent.Length > 0;
            UpdateActions();
            UpdateLayout();
            _focusPending = true;
            DirectoryLoaded?.Invoke(listing);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _generation && !token.IsCancellationRequested && IsEffectivelyVisible)
                {
                    RestorePendingFocus();
                }
            }, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (generation == _generation)
            {
                _busy = false;
                _status.Text = ex.Message;
                Entries.IsEnabled = true;
                UpdateActions();
            }
        }
    }

    private void UpdateActions()
    {
        var entry = Entries.SelectedItem as SteamFileEntry;
        _open.IsEnabled = !_busy && entry is not null && _listing?.Error is null;
        _open.Title = entry?.Folder == true ? "Open folder" : "Select file";
        _selection.Text = entry?.Name ?? "";
        _choose.IsEnabled = !_busy && _listing is { Error: null };
    }

    private void OpenSelection()
    {
        if (_busy || Entries.SelectedItem is not SteamFileEntry entry)
        {
            return;
        }

        if (entry.Folder)
        {
            OpenDirectory(entry.Path);
        }
        else if (!_folder)
        {
            Finish(entry.Path);
        }
    }

    private async Task EnterPathAsync(string value)
    {
        if (_completed)
        {
            return;
        }

        _load?.Cancel();
        var generation = ++_generation;
        try
        {
            var path = Path.GetFullPath(value);
            var kind = await Task.Run(() => Directory.Exists(path) ? 1 : File.Exists(path) ? 2 : 0, _lifetime.Token);
            if (_completed || _lifetime.IsCancellationRequested || generation != _generation)
            {
                return;
            }

            if (kind == 1)
            {
                await ReadDirectoryAsync(path);
            }
            else if (kind == 2 && !_folder && (_extensions.Length == 0 ||
                                               _extensions.Contains(Path.GetExtension(path),
                                                   StringComparer.OrdinalIgnoreCase)))
            {
                Finish(path);
            }
            else
            {
                _status.Text = "The path is unavailable or its file type is not supported.";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
        }
    }

    private void Finish(string? path)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _generation++;
        _load?.Cancel();
        Completed?.Invoke(path);
    }
}
