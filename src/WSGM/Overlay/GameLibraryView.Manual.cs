using System;
using System.Linq;
using System.Threading.Tasks;
using WSGM.Controls;
using WSGM.Core;

namespace WSGM.Overlay;

public sealed partial class GameLibraryView
{
    private ManualShortcutConfig _manualDraft = new();

    private void OpenManualSource(string? id = null)
    {
        _manualDraft = _service?.ReadState().ManualSources?.FirstOrDefault(source => source.Id == id)?.Copy()
                       ?? new ManualShortcutConfig();
        if (_manualDraft.RomPath is not null)
        {
            OpenRomSource(_manualDraft.Id, true);
            return;
        }

        Navigate(RenderManualSource);
    }

    private void RenderManualSource()
    {
        var body = NewStack(_manualDraft.Id.Length == 0 ? "Add a manual shortcut" : "Configure " + _manualDraft.Name);
        body.Children.Add(Caption(
            "Preview this command before applying it to Steam. Its backing file tracks storage availability."));
        AddManualText("Title", _manualDraft.Name, value => _manualDraft.Name = value);
        body.Children.Add(Tagged(Row("Executable", _manualDraft.Target, Icons.Grid4, () =>
                _ = RunSafelyAsync(PickManualPathAsync(false, value => _manualDraft.Target = value),
                    "executable picker")),
            "manual.target"));
        AddManualText("Arguments", _manualDraft.Arguments, value => _manualDraft.Arguments = value);
        body.Children.Add(Tagged(Row("Working folder", _manualDraft.WorkingDirectory, Icons.Grid4, () =>
                _ = RunSafelyAsync(PickManualPathAsync(true, value => _manualDraft.WorkingDirectory = value),
                    "working folder")),
            "manual.working"));
        body.Children.Add(Tagged(Row("Backing game file", _manualDraft.ContentPath, Icons.Grid4, () =>
                _ = RunSafelyAsync(PickManualPathAsync(false, value => _manualDraft.ContentPath = value),
                    "backing file")),
            "manual.content"));
        AddManualText("Location name", _manualDraft.Location, value => _manualDraft.Location = value);
        body.Children.Add(ToggleRow("Track backing storage", _manualDraft.LibraryBacked, value =>
        {
            _manualDraft.LibraryBacked = value;
            RenderManualSource();
        }));
        body.Children.Add(Tagged(PrimaryRow("Add and preview", "Write nothing to Steam until you apply",
            Icons.Play, _manualDraft.Name.Length == 0 || _manualDraft.Target.Length == 0
                ? null
                : () => _ = RunSafelyAsync(SaveManualSourceAsync(), "save manual shortcut")), "manual.save"));
        if (_manualDraft.Id.Length > 0)
        {
            var id = _manualDraft.Id;
            body.Children.Add(Tagged(DangerRow("Remove source", "The imported shortcut and files stay", Icons.Close,
                () => ConfirmCommand("Remove this manual source?", "Stops scanning this source. "
                                                                   + "The imported Steam shortcut and backing files stay.",
                    token => _service!.RemoveManualSourceAsync(id, token))), "manual.remove"));
        }

        SetContent(body);
        return;

        void AddManualText(string name, string value, Action<string> change)
        {
            body.Children.Add(Tagged(Row(name, value, Icons.ListLines, () => EditText(name, value, 0, next =>
            {
                change(next);
                RenderManualSource();
            })), "manual." + name));
        }
    }

    private async Task PickManualPathAsync(bool folder, Action<string> choose)
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(folder);
        if (path is not null && generation == NavigationGeneration)
        {
            choose(path);
            RenderManualSource();
        }
    }

    private Task SaveManualSourceAsync()
    {
        var source = _manualDraft.Copy();
        return RunCommandAsync(token => _service!.AddManualSourceAsync(source, token), "save manual shortcut",
            () => Replace(() => RenderReview(false)));
    }
}
