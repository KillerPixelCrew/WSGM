using System;
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

    private readonly TextBox _name = new() { PlaceholderText = "Profile name", MaxLength = 80 };

    private readonly TextBox _processes = new()
    {
        PlaceholderText = "game.exe\nlauncher.exe", AcceptsReturn = true, MinHeight = 76, MaxHeight = 120,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly ComboBox _profiles = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _save = new() { Content = "Save profile" };
    private readonly PerformanceOverlayBridge _source;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private bool _confirmDelete;
    private GameProfile? _editing;
    private bool _loading;

    internal ApplicationProfilesView(PerformanceOverlayBridge source, CancellationToken cancellationToken)
    {
        _source = source;
        _cancellation = cancellationToken;
        Spacing = 10;
        Classes.Add("profile-editor");
        foreach (var (control, label) in new (Control, string)[]
                 {
                     (_profiles, "Saved profile"), (_name, "Profile name"), (_processes, "Activation processes")
                 })
        {
            AutomationProperties.SetName(control, label);
        }

        var create = new Button { Content = "New profile" };
        create.Click += (_, _) => NewProfile();
        var selection = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        selection.Children.Add(_profiles);
        Grid.SetColumn(create, 1);
        selection.Children.Add(create);
        Children.Add(selection);
        Children.Add(_name);
        Children.Add(new TextBlock { Text = "Activation processes", FontWeight = FontWeight.SemiBold });
        Children.Add(_processes);
        Children.Add(new TextBlock
        {
            Text = "One executable name per line, including .exe. Names match exactly, ignoring case. "
                   + "Existing profiles with an empty list retain their original application binding.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        var current = new Button { Content = "Add current application's process" };
        current.IsEnabled = source.ProfileScope.Target?.RtssProfileName is { Length: > 0 };
        current.Click += (_, _) =>
        {
            if (_source.ProfileScope.Target?.RtssProfileName is { Length: > 0 } executable)
            {
                _processes.Text = string.Join(Environment.NewLine,
                    ProcessNames().Append(executable).Distinct(StringComparer.OrdinalIgnoreCase));
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

    private string[] ProcessNames()
    {
        return (_processes.Text ?? string.Empty).Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
        _name.Text = profile is null ? string.Empty :
            string.IsNullOrEmpty(profile.Name) ? profile.Id : profile.Name;
        _processes.Text = string.Join(Environment.NewLine, profile?.ProcessNames ?? []);
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
            var id = await _source.SaveProfileAsync(_editing?.Id, _name.Text ?? string.Empty, ProcessNames(),
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
