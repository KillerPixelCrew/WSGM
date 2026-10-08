using System;
using System.Linq;
using System.Threading.Tasks;
using WSGM.Controls;

namespace WSGM.Overlay;

public sealed partial class EmulatorManagerView
{
    private void RenderBios()
    {
        var page = _service?.ReadPageState();
        var snapshot = _service?.ReadState();
        if (page is null || snapshot is null)
        {
            RenderMessage("BIOS & firmware", "Emulator information is not available in this session.");
            return;
        }

        var body = NewStack("BIOS & firmware");
        body.Children.Add(ManagerTabs(snapshot, "bios"));
        AddEmulatorStatus(body, snapshot);
        body.Children.Add(Caption("BIOS folder · " + page.Bios.Folder));
        body.Children.Add(
            Caption("EmuDeck layout · MD5 checked against retrobios. Unhashed files are marked Present."));
        Commands(body,
            Tagged(Row("Change…", "", null, snapshot.Busy
                ? null
                : () => _ = RunSafelyAsync(ChooseFolder(), "BIOS folder")), "bios.folder"),
            Tagged(Row("Add files…", "", null, snapshot.Busy
                ? null
                : () => _ = RunSafelyAsync(AddFiles("", false), "BIOS files")), "bios.add"),
            Tagged(Row("Add folder…", "", null, snapshot.Busy
                ? null
                : () => _ = RunSafelyAsync(AddFiles("", true), "BIOS files")), "bios.add.folder"),
            Tagged(Row("Verify", "", null, snapshot.Busy
                ? null
                : () => Run(_service!.VerifyBiosAsync)), "bios.verify"));
        if (!page.Bios.Checked)
        {
            body.Children.Add(Caption("BIOS files have not been checked yet. Choose Verify files."));
        }

        var expanded = page.Bios.Systems.FirstOrDefault(system => system.Status is "Missing" or "Wrong file"
                                                                  && system.Emulators.Length > 0)?.Id ??
                       page.Bios.Systems.FirstOrDefault()?.Id;
        foreach (var system in page.Bios.Systems)
        {
            var id = system.Id;
            var group = Group(body, system.Name, "bios:" + id);
            var section = (CollapsibleSection)body.Children[^1];
            section.IsExpanded = id == expanded;
            section.Summary = system.Status + " · " + (system.Emulators.Length == 0
                ? "Emulator not installed"
                : "Used by " + string.Join(" · ", system.Emulators));
            foreach (var file in system.Files.Where(file => system.Id != "ps2" || file.Status != "Missing").Take(100))
            {
                group.Children.Add(Caption(file.Path + " · " + file.Status + (file.Optional ? " · Optional" : "")
                                           + (file.Md5.Length > 0 ? " · MD5 " + file.Md5 : "")));
            }

            if (id == "ps2" && system.Files.All(file => file.Status == "Missing"))
            {
                group.Children.Add(
                    Caption("Add a supported PlayStation 2 BIOS dump. Regional alternatives are accepted by MD5."));
            }

            foreach (var link in system.Links)
            {
                group.Children.Add(Caption(link));
            }

            Commands(group, Tagged(Row("Add files…", "", null,
                        snapshot.Busy ? null : () => _ = RunSafelyAsync(AddFiles(id, false), "BIOS files")),
                    "bios.add:" + id),
                Tagged(Row("Relink emulators", "", null,
                        snapshot.Busy ? null : () => Run(token => _service!.RelinkBiosAsync(id, token))),
                    "bios.relink:" + id));
            foreach (var installed in snapshot.Installations.Where(item => item.Systems.Contains(id)
                                                                           && item.DataPolicy.Prerequisites.Any(rule =>
                                                                               rule.NativeInstaller)))
            {
                var installationId = installed.Id;
                group.Children.Add(Tagged(Row("Install firmware for " + installed.Name,
                    "Use the locally supplied firmware package", Icons.Grid4,
                    () => Navigate(() => RenderEmulatorSetup(installationId))), "bios.firmware:" + installationId));
            }
        }

        SetContent(body);
    }

    private async Task ChooseFolder()
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(true);
        if (path is not null && generation == NavigationGeneration)
        {
            Run(token => _service!.SetBiosFolderAsync(path, token));
        }
    }

    private async Task AddFiles(string systemId, bool folder)
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(folder);
        if (path is not null && generation == NavigationGeneration)
        {
            Run(token => _service!.AddBiosFilesAsync(path, systemId, token));
        }
    }
}
