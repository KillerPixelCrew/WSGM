namespace WSGM.AllyXLab;

/// <summary>The device check that runs before anything is read: other managers, the hiding driver,
/// and whether this tool can see the real controller at all.</summary>
internal sealed partial class MainForm
{
    private string? _hidHideAdded;

    private async Task DeviceCheckAsync()
    {
        // Process enumeration and each close request block (up to 8 s per manager), so they run off
        // the UI thread; the status text is then painted while the tester waits.
        SessionLog log = new();
        _status.Text = "Looking for other device managers…";
        log.Add("drivers", await Task.Run(Conflicts.Drivers));
        IReadOnlyList<RunningManager> managers = await Task.Run(Conflicts.Running);
        log.Add("managers", managers);
        if (managers.Count > 0)
        {
            string text = string.Join("\n", managers.Select(m => $"• {m.Label} — {m.Why}{(m.Closable ? "" : " (a service; close it yourself if you want it gone)")}"))
                + "\n\nWhile these run, the controller is often hidden from this tool, replaced by a virtual one, or held in another mode.";
            string answer = managers.Any(m => m.Closable)
                ? await Ask("Other managers are running", text, ("close", "Close them for me"), ("continue", "Continue anyway"), ("stop", "Stop and save"))
                : await Ask("A device service is running", text, ("continue", "Continue anyway"), ("stop", "Stop and save"));
            if (answer == "stop")
            {
                _stopping = true;
                CheckStop();
            }

            if (answer == "close")
            {
                foreach (RunningManager manager in managers.Where(m => m.Closable))
                {
                    _status.Text = "Asking " + manager.Label + " to close…";
                    await Task.Run(() => Conflicts.Close(manager, log));
                }

                _status.Text = "";
                IReadOnlyList<RunningManager> left = await Task.Run(Conflicts.Running);
                log.Add("managers-after-close", left);
                if (left.Count > 0)
                {
                    await Ask("Still running", string.Join("\n", left.Select(m => "• " + m.Label))
                        + "\n\nClose these yourself if you can. Captures still run, and the report records what was running.",
                        ("continue", "Continue"));
                }
            }
        }

        HidHideState hidHide = HidHideAccess.Read(log);
        bool listed = HidHideAccess.Contains(hidHide.Applications, Environment.ProcessPath ?? "");
        if (hidHide is { Available: true, Active: true, Inverse: true })
        {
            // In inverse mode the list names the applications that are denied, so adding this tool
            // would hide the controller from it. The lab does not edit an inverse list.
            if (listed)
            {
                string answer = await Ask("HidHide blocks this tool",
                    "HidHide is in inverse mode and lists this tool, so hidden controllers are invisible here."
                    + "\n\nThis tool does not change an inverse-mode list. Remove its entry in the HidHide Configuration Client if the capture should see them.",
                    ("continue", "Continue anyway"), ("stop", "Stop and save"));
                if (answer == "stop")
                {
                    _stopping = true;
                    CheckStop();
                }
            }
        }
        else if (hidHide is { Available: true, Active: true } && !listed)
        {
            string answer = await Ask("HidHide is hiding controllers",
                "HidHide is active and this tool is not on its allowed list, so the controller may be invisible here or replaced by a virtual one."
                + "\n\nMay this tool add itself for the session? Its entry is removed again when the session ends; other entries are left alone.",
                ("allow", "Add this tool, then undo it later"), ("skip", "Leave HidHide alone"), ("stop", "Stop and save"));
            if (answer == "stop")
            {
                _stopping = true;
                CheckStop();
            }

            if (answer == "allow")
            {
                try
                {
                    _hidHideAdded = HidHideAccess.TryAllow(log);
                    _session.Observation("HidHide allowance", new { Added = _hidHideAdded is not null });
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    log.Add("hidhide-allow-failed", e.Message);
                    await Ask("HidHide could not be changed", e.Message + "\n\nNothing was changed. The capture continues; a hidden controller stays hidden.", ("continue", "Continue"));
                }
            }
        }

        _session.RecordLocal(new Result(new Request(ActionKind.Inventory, "Device access check", Seconds: 1), "observed", "not-needed", log.Events, null));
    }

    private void RestoreHidHide()
    {
        if (_hidHideAdded is not { } added)
        {
            return;
        }

        SessionLog log = new();
        bool restored = HidHideAccess.Restore(added, log);
        _hidHideAdded = null;
        _session.RecordLocal(new Result(new Request(ActionKind.Inventory, "HidHide restoration", Seconds: 1),
            restored ? "entry-removed-readback-matched" : "RESTORATION FAILED", restored ? "restored" : "unverified", log.Events, null));
        if (!restored)
        {
            _session.Observation("HidHide restoration", "This tool's entry is still on the allowed-application list, or the list could not be read. Remove the entry in the HidHide Configuration Client.");
        }
    }
}
