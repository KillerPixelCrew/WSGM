using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Edits game profiles' names, activation processes and switches without the game running.</summary>
/// <remarks>
///     Values are not edited here. They are set on their own overlay and Quick Access rows while the game
///     runs, and a game profile holds only those; everything else comes from Global.
/// </remarks>
internal sealed class ApplicationProfilesView : StackPanel
{
    private readonly CancellationToken _cancellation;
    private readonly Button _delete = new() { Content = "Delete profile", IsEnabled = false };
    private readonly CheckBox _enabled = new() { Content = "Use this profile when a matching application is active" };

    // Press-to-edit rows: the controller cannot reach a TextBox, so each field opens the overlay keyboard.
    private readonly Button _name = Row();
    private readonly List<string> _processNames = [];
    private readonly StackPanel _processRows = new() { Spacing = 6 };
    private readonly ComboBox _profiles = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _save = new() { Content = "Save profile" };
    private readonly PerformanceOverlayBridge _source;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private bool _confirmDelete;
    private GameProfile? _editing;
    private bool _loading;
    private string _nameDraft = string.Empty;

    internal ApplicationProfilesView(PerformanceOverlayBridge source, CancellationToken cancellationToken)
    {
        _source = source;
        _cancellation = cancellationToken;
        Spacing = 10;
        Classes.Add("profile-editor");
        AutomationProperties.SetName(_profiles, "Saved profile");
        AutomationProperties.SetName(_name, "Profile name");
        _name.Click += (_, _) => RequestText("Profile name", _nameDraft, SetName);
        var addProcess = Row();
        addProcess.Content = "Add process";
        addProcess.Click += (_, _) => RequestText("Process name", string.Empty, AddProcess);
        var create = new Button { Content = "New profile" };
        create.Click += (_, _) => NewProfile();
        var selection = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        selection.Children.Add(_profiles);
        Grid.SetColumn(create, 1);
        selection.Children.Add(create);
        Children.Add(selection);
        Children.Add(_name);
        Children.Add(new TextBlock { Text = "Activation processes", FontWeight = FontWeight.SemiBold });
        Children.Add(_processRows);
        Children.Add(addProcess);
        Children.Add(new TextBlock
        {
            Text = "One executable name per row, including .exe. Names match exactly, ignoring case. "
                   + "Existing profiles with an empty list retain their original application binding.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        var current = new Button { Content = "Add current application's process" };
        current.IsEnabled = source.ProfileScope.Target?.RtssProfileName is { Length: > 0 };
        current.Click += (_, _) =>
        {
            if (_source.ProfileScope.Target?.RtssProfileName is { Length: > 0 } executable)
            {
                AddProcess(executable);
            }
        };
        Children.Add(current);
        Children.Add(_enabled);
        Children.Add(new TextBlock
        {
            Text = "A game profile holds only what you change on the overlay or Quick Access rows while it is "
                   + "active. Everything else comes from Global. Switching it off keeps its values.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        Children.Add(new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _save, _delete } });
        Children.Add(_status);
        _profiles.SelectionChanged += (_, _) =>
        {
            if (!_loading && _profiles.SelectedItem is ComboBoxItem { Tag: GameProfile entry })
            {
                Load(entry);
            }
        };
        _save.Click += async (_, _) => await SaveAsync();
        _delete.Click += async (_, _) =>
        {
            if (!_confirmDelete)
            {
                _confirmDelete = true;
                _delete.Content = "Confirm delete";
                _status.Text = "Deleting removes this saved profile. Matching applications will use Global.";
                return;
            }

            if (_editing is { } entry)
            {
                await RunAsync(async () =>
                {
                    await _source.DeleteProfileAsync(entry.Id, _cancellation);
                    Reload(null);
                    _status.Text = "Profile deleted.";
                });
            }
        };
        foreach (var button in new[] { create, current, _save, _delete })
        {
            button.Classes.Add("deck-action");
        }

        Reload(source.ProfileSnapshot.Active.GameProfileId);
    }

    internal Control DefaultFocusTarget => _profiles;

    private static Button Row()
    {
        var row = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left
        };
        row.Classes.Add("deck-action");
        return row;
    }

    private void RequestText(string prompt, string initial, Action<string> accept)
    {
        if (TopLevel.GetTopLevel(this) is not OverlayWindow window || !window.RequestText(prompt, initial, 0, accept))
        {
            _status.Text = "Keyboard unavailable. Reopen the overlay to retry.";
        }
    }

    private void SetName(string name)
    {
        _nameDraft = name;
        _name.Content = name.Length == 0 ? "Profile name" : name;
    }

    private void AddProcess(string value)
    {
        var name = value.Trim();
        if (name.Length > 0 && !_processNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            _processNames.Add(name);
            RenderProcesses();
        }
    }

    private void EditProcess(Button row, string value)
    {
        var index = _processRows.Children.IndexOf(row);
        if (index < 0 || index >= _processNames.Count)
        {
            return;
        }

        var name = value.Trim();
        if (name.Length == 0)
        {
            _processNames.RemoveAt(index);
        }
        else
        {
            _processNames[index] = name;
        }

        RenderProcesses();
    }

    // Rows are reused by position so the row that opened the keyboard keeps focus when it closes.
    private void RenderProcesses()
    {
        while (_processRows.Children.Count > _processNames.Count)
        {
            _processRows.Children.RemoveAt(_processRows.Children.Count - 1);
        }

        for (var index = 0; index < _processNames.Count; index++)
        {
            if (index == _processRows.Children.Count)
            {
                var row = Row();
                row.Click += (_, _) =>
                {
                    var position = _processRows.Children.IndexOf(row);
                    if (position >= 0 && position < _processNames.Count)
                    {
                        RequestText("Process name", _processNames[position], value => EditProcess(row, value));
                    }
                };
                _processRows.Children.Add(row);
            }

            ((Button)_processRows.Children[index]).Content = _processNames[index];
        }
    }

    private void Reload(string? selectedId)
    {
        _loading = true;
        var entries = _source.Profiles.Select(entry => new ComboBoxItem
        {
            Content = string.IsNullOrEmpty(entry.Name) ? entry.Id : entry.Name, Tag = entry
        }).ToArray();
        _profiles.ItemsSource = entries;
        _profiles.SelectedItem =
            entries.FirstOrDefault(item => ((GameProfile)item.Tag!).Id == selectedId)
            ?? entries.FirstOrDefault();
        _loading = false;
        if (_profiles.SelectedItem is ComboBoxItem { Tag: GameProfile entry })
        {
            Load(entry);
        }
        else
        {
            NewProfile();
        }
    }

    private void NewProfile()
    {
        _loading = true;
        _profiles.SelectedIndex = -1;
        _loading = false;
        Load(null);
        _name.Focus();
    }

    private void Load(GameProfile? profile)
    {
        _editing = profile;
        SetName(profile is null ? string.Empty :
            string.IsNullOrEmpty(profile.Name) ? profile.Id : profile.Name);
        _processNames.Clear();
        _processNames.AddRange(profile?.ProcessNames ?? []);
        RenderProcesses();
        _enabled.IsChecked = profile?.Enabled ?? true;
        _confirmDelete = false;
        _delete.Content = "Delete profile";
        _delete.IsEnabled = profile is not null;
        _status.Text = string.Empty;
    }

    private async Task SaveAsync()
    {
        await RunAsync(async () =>
        {
            var id = await _source.SaveProfileAsync(_editing?.Id, _nameDraft, _processNames.ToArray(),
                _enabled.IsChecked == true, _cancellation);
            Reload(id);
            _status.Text = "Profile saved.";
        });
    }

    private async Task RunAsync(Func<Task> operation)
    {
        IsEnabled = false;
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            IsEnabled = true;
        }
    }
}
