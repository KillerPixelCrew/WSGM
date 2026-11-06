using WSGM.Core;

namespace WSGM.Settings;

/// <summary>Editable settings for one program launched after the shell starts.</summary>
public sealed class StartupAppRow : ObservableObject
{
    /// <summary>Gets or sets the executable or protocol to launch.</summary>
    public string Path
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(Path)))
            {
                Raise(nameof(Name));
                Raise(nameof(Detail));
            }
        }
    } = "";

    /// <summary>Gets or sets the command-line arguments passed to the program.</summary>
    public string Args
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(Args)))
            {
                Raise(nameof(Detail));
            }
        }
    } = "";

    /// <summary>The name the user gave the app, or empty for its file name.</summary>
    public string Label
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(Label)))
            {
                Raise(nameof(Name));
            }
        }
    } = "";

    /// <summary>What the card calls the app: its label, or its program's file name without the extension.</summary>
    public string Name
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Label))
            {
                return Label.Trim();
            }

            var path = Path.Trim().Trim('"');
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            return name.Length > 0 ? name : path.Length > 0 ? path : "New app";
        }
    }

    /// <summary>The program and its arguments, or why it will be skipped.</summary>
    public string Detail => Missing ? $"File not found: {Path}"
        : Args.Length == 0 ? Path : $"{Path}  ·  {Args}";

    /// <summary>Whether the program's file is missing, so startup skips it.</summary>
    public bool Missing
    {
        get;
        internal set
        {
            if (SetFieldIfChanged(ref field, value, nameof(Missing)))
            {
                Raise(nameof(Detail));
            }
        }
    }

    /// <summary>The program's place in the startup order, from 1.</summary>
    public int Number
    {
        get;
        internal set => SetField(ref field, value, nameof(Number));
    }

    /// <summary>Whether the card shows its actions: edit, move and remove.</summary>
    public bool ShowActions
    {
        get;
        set
        {
            SetField(ref field, value, nameof(ShowActions));
            if (!value)
            {
                ConfirmRemove = false;
            }
        }
    }

    /// <summary>Whether Remove waits for its confirmation.</summary>
    public bool ConfirmRemove
    {
        get;
        set => SetField(ref field, value, nameof(ConfirmRemove));
    }

    /// <summary>Gets or sets whether this program participates in startup.</summary>
    public bool Enabled
    {
        get;
        set => SetField(ref field, value, nameof(Enabled));
    } = true;

    /// <summary>Gets or sets whether the program needs an elevated launch.</summary>
    public bool Elevated
    {
        get;
        set => SetField(ref field, value, nameof(Elevated));
    }

    /// <summary>Gets or sets whether the program is watched and restarted when it exits.</summary>
    public bool AutoRelaunch
    {
        get;
        set => SetField(ref field, value, nameof(AutoRelaunch));
    }
}
