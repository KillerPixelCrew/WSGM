using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using WSGM.Core;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    /// <summary>Gets the command that removes one startup-program row.</summary>
    public RelayCommand<StartupAppRow> RemoveAppCommand { get; }

    /// <summary>Gets the command that moves one startup-program row up.</summary>
    public RelayCommand<StartupAppRow> MoveUpCommand { get; }

    /// <summary>Gets the command that moves one startup-program row down.</summary>
    public RelayCommand<StartupAppRow> MoveDownCommand { get; }

    /// <summary>Companion apps found on this PC, which Add app offers before a file picker.</summary>
    public IReadOnlyList<StartupSuggestion> StartupSuggestions { get; private set; } = [];

    /// <summary>Whether no companion app was found, so Add app offers only the file picker.</summary>
    public bool NoStartupSuggestions => StartupSuggestions.Count == 0;

    /// <summary>The startup app the editor shows.</summary>
    public StartupAppRow? EditedStartupApp
    {
        get;
        set => SetField(ref field, value, nameof(EditedStartupApp));
    }

    /// <summary>Whether there are no startup apps, so the list shows how to add one.</summary>
    public bool NoStartupApps => StartupApps.Count == 0;

    // --- Startup apps ---
    /// <summary>Gets the ordered startup programs shown in the settings editor.</summary>
    public ObservableCollection<StartupAppRow> StartupApps { get; } = [];

    /// <summary>Gets or sets the initial delay before launching configured startup programs.</summary>
    public int StartupDelayMs
    {
        get;
        set => SetField(ref field, value, nameof(StartupDelayMs));
    }

    /// <summary>Gets or sets the delay between successive configured startup programs.</summary>
    public int StaggerDelayMs
    {
        get;
        set => SetField(ref field, value, nameof(StaggerDelayMs));
    }

    /// <summary>Gets or sets whether a splash window is shown while game mode starts.</summary>
    public bool BootSplashEnabled
    {
        get;
        set => SetField(ref field, value, nameof(BootSplashEnabled));
    }

    private void BuildStartupSuggestions()
    {
        List<StartupSuggestion> found = [];
        foreach (var (label, path, elevated) in _services.DetectStartupApps())
        {
            found.Add(new StartupSuggestion(label, path, elevated));
        }

        StartupSuggestions = found;
    }

    /// <summary>Adds a program to the end of the startup order, switched on.</summary>
    /// <param name="path">The program.</param>
    /// <param name="elevated">Whether it runs as administrator.</param>
    /// <returns>The new row, for the editor to open.</returns>
    /// <param name="label">What the app is called, or empty for its file name.</param>
    public StartupAppRow AddStartupApp(string path, bool elevated, string label = "")
    {
        StartupAppRow row = new() { Label = label, Path = path, Elevated = elevated, Enabled = true };
        StartupApps.Add(row);
        return row;
    }

    /// <summary>Numbers the rows and checks each program's file, after a change to the list or a path.</summary>
    private void OnStartupAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.NewItems?.OfType<StartupAppRow>() ?? [])
        {
            row.PropertyChanged += OnStartupAppChanged;
            CheckStartupApp(row);
        }

        foreach (var row in e.OldItems?.OfType<StartupAppRow>() ?? [])
        {
            row.PropertyChanged -= OnStartupAppChanged;
        }

        for (var index = 0; index < StartupApps.Count; index++)
        {
            StartupApps[index].Number = index + 1;
        }

        Raise(nameof(NoStartupApps));
    }

    private void OnStartupAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is StartupAppRow row && e.PropertyName == nameof(StartupAppRow.Path))
        {
            CheckStartupApp(row);
        }
    }

    private void CheckStartupApp(StartupAppRow row)
    {
        row.Missing = !string.IsNullOrWhiteSpace(row.Path) && !_services.ProgramExists(row.Path);
    }

    /// <summary>Moves a startup-program row by one position when the target remains in range.</summary>
    /// <param name="row">The row to move, or null (a no-op).</param>
    /// <param name="delta">The signed number of positions to move the row.</param>
    private void MoveStartupApp(StartupAppRow? row, int delta)
    {
        if (row is null)
        {
            return;
        }

        var index = StartupApps.IndexOf(row);
        var target = index + delta;
        if (index >= 0 && target >= 0 && target < StartupApps.Count)
        {
            StartupApps.Move(index, target);
        }
    }
}
