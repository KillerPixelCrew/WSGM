using System;
using WSGM.Core;

namespace WSGM.Settings;

/// <summary>One widget of the custom performance overlay level: its name, its detail and whether it is being moved.</summary>
public sealed class OsdWidgetRow : ObservableObject
{
    private readonly Action _changed;

    /// <summary>Creates the row.</summary>
    /// <param name="key">The widget's name in <see cref="PerformanceConfig.OsdCustomOrder" />.</param>
    /// <param name="name">What the row is called.</param>
    /// <param name="level">0 hidden, 1 minimal, 2 full.</param>
    /// <param name="changed">Called after the user changes the detail.</param>
    internal OsdWidgetRow(string key, string name, int level, Action changed)
    {
        Key = key;
        Name = name;
        _changed = changed;
        Level = Math.Clamp(level, 0, 2);
    }

    /// <summary>The widget's name in the saved order.</summary>
    public string Key { get; }

    /// <summary>What the row is called.</summary>
    public string Name { get; }

    /// <summary>0 hidden, 1 minimal, 2 full.</summary>
    public int Level
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            Raise(nameof(Level));
            Raise(nameof(IsHidden));
            Raise(nameof(IsMinimal));
            Raise(nameof(IsFull));
        }
    }

    /// <summary>Whether the widget is left out.</summary>
    public bool IsHidden
    {
        get => Level == 0;
        set => Choose(value, 0);
    }

    /// <summary>Whether the widget shows its short form.</summary>
    public bool IsMinimal
    {
        get => Level == 1;
        set => Choose(value, 1);
    }

    /// <summary>Whether the widget shows everything it has.</summary>
    public bool IsFull
    {
        get => Level == 2;
        set => Choose(value, 2);
    }

    /// <summary>Whether the row is picked up, so Up and Down move it.</summary>
    public bool IsMoving
    {
        get;
        set => SetField(ref field, value, nameof(IsMoving));
    }

    private void Choose(bool chosen, int level)
    {
        if (!chosen || Level == level)
        {
            return;
        }

        Level = level;
        _changed();
    }
}
