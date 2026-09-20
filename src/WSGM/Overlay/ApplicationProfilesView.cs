using System;
using System.Globalization;
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

/// <summary>Edits durable profile activation rules without requiring the application to be running.</summary>
internal sealed class ApplicationProfilesView : StackPanel
{
    private readonly CancellationToken _cancellation;
    private readonly Button _delete = new() { Content = "Delete profile", IsEnabled = false };
    private readonly CheckBox _enabled = new() { Content = "Use this profile when a matching application is active" };

    private readonly TextBox _frameLimit = new()
        { PlaceholderText = "Inherit Global", HorizontalAlignment = HorizontalAlignment.Stretch };

    private readonly TextBox _name = new() { PlaceholderText = "Profile name", MaxLength = 80 };

    private readonly ComboBox _overlay = new()
    {
        ItemsSource = new[] { "Inherit Global", "Off", "Minimal", "Extended", "Full", "Custom" },
        SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch
    };

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
    private PerformanceApplicationPolicy? _editing;
    private bool _loading;

    internal ApplicationProfilesView(PerformanceOverlayBridge source, CancellationToken cancellationToken)
    {
        _source = source;
        _cancellation = cancellationToken;
        Spacing = 10;
        Classes.Add("profile-editor");
        foreach (var (control, label) in new (Control, string)[]
                 {
                     (_profiles, "Saved profile"), (_name, "Profile name"), (_processes, "Activation processes"),
                     (_frameLimit, "Profile frame limit"), (_overlay, "Profile overlay level")
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
        var values = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        values.Children.Add(new StackPanel
        {
            Spacing = 4, Children =
            {
                new TextBlock { Text = "Frame limit (0 = unlimited)" }, _frameLimit
            }
        });
        var overlay = new StackPanel
        {
            Spacing = 4, Children =
            {
                new TextBlock { Text = "Performance overlay" }, _overlay
            }
        };
        Grid.SetColumn(overlay, 1);
        values.Children.Add(overlay);
        Children.Add(values);
        Children.Add(new TextBlock
        {
            Text = "Other supported device settings are saved from their overlay pages while this profile is active. "
                   + "Selecting Global preserves this profile's values.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        Children.Add(new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _save, _delete } });
        Children.Add(_status);
        _profiles.SelectionChanged += (_, _) =>
        {
            if (!_loading && _profiles.SelectedItem is ComboBoxItem { Tag: PerformanceApplicationPolicy entry })
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
                    await _source.DeleteProfileAsync(entry.ApplicationId, _cancellation);
                    Reload(null);
                    _status.Text = "Profile deleted.";
                });
            }
        };
        foreach (var button in new[] { create, current, _save, _delete })
        {
            button.Classes.Add("deck-action");
        }

        var target = source.ProfileScope.Target;
        var active = ApplicationProfileRules.Match(source.Profiles, target?.ApplicationId, target?.RtssProfileName,
            item => item.ApplicationId, item => item.ProcessNames);
        Reload(active?.ApplicationId);
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
            Content = string.IsNullOrEmpty(entry.Name) ? entry.ApplicationId : entry.Name, Tag = entry
        }).ToArray();
        _profiles.ItemsSource = entries;
        _profiles.SelectedItem =
            entries.FirstOrDefault(item => ((PerformanceApplicationPolicy)item.Tag!).ApplicationId == selectedId)
            ?? entries.FirstOrDefault();
        _loading = false;
        if (_profiles.SelectedItem is ComboBoxItem { Tag: PerformanceApplicationPolicy entry })
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

    private void Load(PerformanceApplicationPolicy? profile)
    {
        _editing = profile;
        _name.Text = profile is null ? string.Empty :
            string.IsNullOrEmpty(profile.Name) ? profile.ApplicationId : profile.Name;
        _processes.Text = string.Join(Environment.NewLine, profile?.ProcessNames ?? []);
        _enabled.IsChecked = profile?.Enabled ?? true;
        _frameLimit.Text = profile?.Values.FrameLimit?.ToString(CultureInfo.InvariantCulture);
        _overlay.SelectedIndex = profile?.Values.OverlayLevel is { } level ? level + 1 : 0;
        _confirmDelete = false;
        _delete.Content = "Delete profile";
        _delete.IsEnabled = profile is not null;
        _status.Text = string.Empty;
    }

    private async Task SaveAsync()
    {
        await RunAsync(async () =>
        {
            int? frameLimit = null;
            if (!string.IsNullOrWhiteSpace(_frameLimit.Text))
            {
                if (!int.TryParse(_frameLimit.Text, out var parsed))
                {
                    throw new ArgumentException(
                        "Enter a whole-number frame limit, or leave it empty to inherit Global.");
                }

                frameLimit = parsed;
            }

            var entry = (_editing ?? new PerformanceApplicationPolicy("profile:" + Guid.NewGuid().ToString("N"),
                    string.Empty, PerformanceValues.Empty)) with
                {
                    Name = _name.Text ?? string.Empty, ProcessNames = ProcessNames(),
                    Enabled = _enabled.IsChecked == true,
                    Values = new PerformanceValues(frameLimit,
                        _overlay.SelectedIndex > 0 ? _overlay.SelectedIndex - 1 : null)
                };
            await _source.SaveProfileAsync(entry, _cancellation);
            Reload(entry.ApplicationId);
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
