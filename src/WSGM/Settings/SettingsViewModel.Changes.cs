using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using WSGM.Core;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    /// <summary>What Save would write, as of the last load or save, or null before the first record.</summary>
    private JsonObject? _savedState;

    /// <summary>Gets how many saved values differ from what this window last loaded or saved.</summary>
    public int UnsavedChangeCount
    {
        get;
        private set
        {
            if (SetFieldIfChanged(ref field, value, nameof(UnsavedChangeCount)))
            {
                Raise(nameof(HasUnsavedChanges));
                Raise(nameof(UnsavedChangesText));
            }
        }
    }

    /// <summary>Gets whether Save would write anything.</summary>
    public bool HasUnsavedChanges => UnsavedChangeCount > 0;

    /// <summary>Gets the footer's account of what Save would write.</summary>
    public string UnsavedChangesText => UnsavedChangeCount switch
    {
        0 => "No unsaved changes",
        1 => "1 unsaved change",
        var count => $"{count} unsaved changes"
    };

    /// <summary>Takes what Save would write now as the saved state, so nothing reads as changed.</summary>
    /// <remarks>Called once the window's first reads have settled and after every save.</remarks>
    internal void RecordSavedState()
    {
        RecordSavedState(CaptureSaveRequest());
    }

    /// <summary>Counts again what Save would write that differs from the saved state.</summary>
    /// <remarks>
    ///     Measured on the request Save itself captures, so a change counts exactly when Save would write
    ///     it, wherever on the page it was made.
    /// </remarks>
    internal void RefreshUnsavedChanges()
    {
        if (_savedState is null || IsSaving)
        {
            return;
        }

        UnsavedChangeCount = CountDifferences(_savedState, StateOf(CaptureSaveRequest()));
    }

    private void RecordSavedState(SaveRequest request)
    {
        _savedState = StateOf(request);
        UnsavedChangeCount = 0;
    }

    private static JsonObject StateOf(SaveRequest request)
    {
        JsonObject state = new()
        {
            ["values"] = JsonSerializer.SerializeToNode(request.Values, ConfigJsonContext.Default.AppConfig),
            ["forgotten"] = new JsonArray([.. request.ForgottenDisplays.Select(display => JsonValue.Create(display.DevicePath))]),
            ["plugins"] = new JsonArray(
                [.. request.CommonPluginEdits.Select(edit => JsonValue.Create(edit.ToString()))])
        };
        if (request.DeviceProfiles is { } profiles)
        {
            state["profiles"] = JsonSerializer.SerializeToNode(profiles.ToList(),
                ConfigJsonContext.Default.ListDeviceAuthoredProfile);
        }

        return state;
    }

    /// <summary>Counts the values that differ; a list counts once, so reordering it is one change.</summary>
    private static int CountDifferences(JsonNode? saved, JsonNode? current)
    {
        if (saved is JsonObject savedObject && current is JsonObject currentObject)
        {
            var count = 0;
            foreach (var name in savedObject.Select(pair => pair.Key).Union(currentObject.Select(pair => pair.Key)))
            {
                count += CountDifferences(savedObject[name], currentObject[name]);
            }

            return count;
        }

        return JsonNode.DeepEquals(saved, current) ? 0 : 1;
    }
}
