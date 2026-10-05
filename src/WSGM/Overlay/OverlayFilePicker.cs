using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>A controller-accessible local path picker with no Explorer lifetime.</summary>
internal sealed class OverlayFilePicker : UserControl
{
    private readonly HashSet<string> _extensions;
    private readonly bool _folder;
    private string? _directory;
    private long _generation;
    private string? _focusEntry;
    private CancellationTokenSource? _load;
    private int _shown = 100;

    internal OverlayFilePicker(bool folder, IEnumerable<string> extensions)
    {
        _folder = folder;
        _extensions = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        AttachedToVisualTree += (_, _) => ShowDirectory(null, null);
        DetachedFromVisualTree += (_, _) =>
        {
            _generation++;
            _load?.Cancel();
            _load?.Dispose();
            _load = null;
            Completed?.Invoke(null);
        };
    }

    internal event Action<string?>? Completed;
    internal event Action<string, Action<string>>? TextEntryRequested;

    /// <summary>Lists a folder, or the drives for null.</summary>
    /// <param name="path">The folder.</param>
    /// <param name="focus">
    ///     The entry to focus once listed: empty for the first one, or null to leave focus where the
    ///     surface put it.
    /// </param>
    private void ShowDirectory(string? path, string? focus = "")
    {
        _focusEntry = focus;
        _load?.Cancel();
        _load?.Dispose();
        _load = new CancellationTokenSource();
        var token = _load.Token;
        var generation = ++_generation;
        _directory = path;
        var body = Header(path);
        body.Children.Add(new TextBlock { Text = "Reading folders…" });
        Content = body;
        _ = ReadAsync(path, generation, token);
    }

    private StackPanel Header(string? path)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = path ?? "Drives", TextWrapping = TextWrapping.Wrap });
        Add(body, "Cancel", () => Completed?.Invoke(null));
        Add(body, "Enter a path", () => TextEntryRequested?.Invoke(path ?? "", entered =>
        {
            try
            {
                var absolute = Path.GetFullPath(entered);
                if (Directory.Exists(absolute))
                {
                    ShowDirectory(absolute);
                }
                else if (!_folder && File.Exists(absolute) && Accepts(absolute))
                {
                    Completed?.Invoke(absolute);
                }
                else
                {
                    ShowError("The path is unavailable or its file type is not supported.");
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                ShowError(ex.Message);
            }
        }));
        if (path is not null)
        {
            Add(body, "Drives", () =>
            {
                _shown = 100;
                ShowDirectory(null);
            });
            var parent = Directory.GetParent(path)?.FullName;
            Add(body, "Up", () =>
            {
                _shown = 100;
                ShowDirectory(parent);
            });
            if (_folder)
            {
                Add(body, "Choose this folder", () => Completed?.Invoke(path));
            }
        }

        return body;
    }

    private async Task ReadAsync(string? path, long generation, CancellationToken token)
    {
        try
        {
            var entries = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (path is null)
                {
                    return DriveInfo.GetDrives().Select(drive => (drive.Name, Folder: true)).ToArray();
                }

                return Directory.EnumerateDirectories(path).Select(directory => (directory, Folder: true))
                    .Concat(_folder
                        ? []
                        : Directory.EnumerateFiles(path).Where(Accepts).Select(file => (file, Folder: false)))
                    .OrderByDescending(entry => entry.Folder)
                    .ThenBy(entry => entry.Item1, StringComparer.OrdinalIgnoreCase)
                    .Take(_shown + 1).ToArray();
            }, token);
            if (generation != _generation || token.IsCancellationRequested)
            {
                return;
            }

            var body = Header(path);
            foreach (var entry in entries.Take(_shown))
            {
                var chosen = entry.Item1;
                Add(body, path is null ? chosen : Path.GetFileName(chosen), chosen, () =>
                {
                    if (entry.Folder)
                    {
                        _shown = 100;
                        ShowDirectory(chosen);
                    }
                    else
                    {
                        Completed?.Invoke(chosen);
                    }
                });
            }

            if (entries.Length > _shown)
            {
                var added = entries[_shown].Item1;
                Add(body, "Show more", () =>
                {
                    _shown += 100;
                    ShowDirectory(path, added);
                });
            }

            if (entries.Length == 0)
            {
                body.Children.Add(new TextBlock { Text = "No matching entries." });
            }

            Content = body;
            // A new folder starts on its first entry and "Show more" on the first entry it added;
            // the controller would otherwise fall back to the top of the surface.
            var focus = _focusEntry is "" ? entries.FirstOrDefault().Item1 : _focusEntry;
            _focusEntry = null;
            if (focus is not null)
            {
                PagedRows.FocusAdded(body, focus);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (generation == _generation)
            {
                ShowError(ex.Message);
            }
        }
    }

    private bool Accepts(string path)
    {
        return _extensions.Count == 0 || _extensions.Contains(Path.GetExtension(path));
    }

    private void ShowError(string text)
    {
        var body = Header(_directory);
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        Add(body, "Retry", () => ShowDirectory(_directory));
        Content = body;
    }

    private static void Add(StackPanel body, string label, Action action)
    {
        Add(body, label, null, action);
    }

    private static void Add(StackPanel body, string label, string? tag, Action action)
    {
        var button = new ActionButton { Title = label, Tag = tag };
        button.Click += (_, _) => action();
        body.Children.Add(button);
    }
}
