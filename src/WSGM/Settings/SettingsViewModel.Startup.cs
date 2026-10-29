using System.Collections.Generic;
using System.Collections.ObjectModel;
using WSGM.Core;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    private int _selectedSuggestionIndex;
    private List<(string Path, bool Elevated)> _startupSuggestionTargets = [];

    /// <summary>Gets the command that removes one startup-program row.</summary>
    public RelayCommand<StartupAppRow> RemoveAppCommand { get; }

    /// <summary>Gets the command that moves one startup-program row up.</summary>
    public RelayCommand<StartupAppRow> MoveUpCommand { get; }

    /// <summary>Gets the command that moves one startup-program row down.</summary>
    public RelayCommand<StartupAppRow> MoveDownCommand { get; }

    // --- Startup app suggestions ---
    /// <summary>
    ///     Common handheld companions found on this PC, offered as one-click adds
    ///     instead of making the user hunt for exe paths.
    /// </summary>
    public List<string> StartupSuggestions { get; private set; } = [];

    /// <summary>Gets or sets the selected discovered startup-app suggestion.</summary>
    public int SelectedSuggestionIndex
    {
        get => _selectedSuggestionIndex;
        set => SetField(ref _selectedSuggestionIndex, value, nameof(SelectedSuggestionIndex));
    }

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
        var names = new List<string>();
        var targets = new List<(string, bool)>();

        foreach (var (label, path, elevated) in _services.DetectStartupApps())
        {
            names.Add(label);
            targets.Add((path, elevated));
        }

        names.Add("Choose a program…");
        targets.Add(("", false));

        StartupSuggestions = names;
        _startupSuggestionTargets = targets;
        _selectedSuggestionIndex = 0;
    }

    /// <summary>Adds the selected discovered program when it has a concrete executable path.</summary>
    /// <returns><see langword="true" /> when a startup row was added; otherwise the caller should open a file picker.</returns>
    public bool AddSelectedStartupApp()
    {
        if (_selectedSuggestionIndex < 0 || _selectedSuggestionIndex >= _startupSuggestionTargets.Count)
        {
            return false;
        }

        var (path, elevated) = _startupSuggestionTargets[_selectedSuggestionIndex];
        if (string.IsNullOrEmpty(path))
        {
            return false; // caller opens the file picker
        }

        StartupApps.Add(new StartupAppRow { Path = path, Elevated = elevated, Enabled = true });
        return true;
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
