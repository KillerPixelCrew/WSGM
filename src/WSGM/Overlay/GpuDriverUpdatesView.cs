using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Device > GPU's update workflow, sharing the session service with Steam's Display page.</summary>
public sealed class GpuDriverUpdatesView : ServiceSubView
{
    private IGpuDriverUpdateSource? _service;
    private string? _notes;

    /// <inheritdoc />
    protected override string LogScope => "GPU driver updates";

    internal void Attach(IGpuDriverUpdateSource? service)
    {
        _service = service;
        AttachSource(service);
        if (service is not null)
        {
            Open();
        }
    }

    /// <inheritdoc />
    private protected override void RenderHome()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(SectionLabel("Driver updates"));
        if (_service is not { } service)
        {
            stack.Children.Add(Caption("The GPU update service is unavailable."));
            SetContent(stack);
            return;
        }

        var state = service.Snapshot();
        var busy = state.Any(item => item.Busy);
        stack.Children.Add(Keyed(Row("Check for driver updates", "Checks your GPU vendor's official catalog.",
            Icons.Restart, busy ? null : () => service.Check()), "gpu-updates.check"));
        if (state.Count == 0)
        {
            stack.Children.Add(Caption(service.DiscoveryDetail ?? "No supported Intel, AMD or NVIDIA adapter was found."));
        }

        foreach (var item in state)
        {
            var id = item.Adapter.InstanceId;
            var card = new StackPanel { Spacing = 8, Tag = "gpu-updates.adapter:" + id };
            card.Children.Add(SectionLabel(item.Adapter.Name));
            card.Children.Add(Caption($"Installed: {item.Adapter.Version} · {item.Adapter.Date?.ToString("d") ?? "Date unknown"}"));
            card.Children.Add(Caption(item.Stage + ": " + item.Detail));
            if (item.Stage is "Downloading" or "Verifying" or "Installing")
            {
                card.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = item.Progress,
                    IsIndeterminate = item.Stage != "Downloading", Height = 8 });
            }

            if (item.CanInstall)
            {
                var refusal = service.InstallRefusal(item.Adapter);
                card.Children.Add(Keyed(PrimaryRow("Download and install", refusal ?? "Install the verified vendor package.",
                    Icons.Monitor, busy || refusal is not null ? null : () => ConfirmInstall(id, item)),
                    "gpu-updates.install:" + id));
            }

            if (item.Package is { } package)
            {
                card.Children.Add(Keyed(Row("Release notes and license", package.Details.Host, Icons.Monitor, () =>
                {
                    _notes = _notes == id ? null : id;
                    RenderHome();
                }), "gpu-updates.notes:" + id));
                if (_notes == id)
                {
                    card.Children.Add(new ScrollViewer { MaxHeight = 240,
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content = Caption(package.ReleaseNotes + "\n\nLicense and details: " + package.Details) });
                }
            }

            if (item.Stage is "Downloading" or "Checking" or "Verifying")
            {
                card.Children.Add(Keyed(Row("Cancel", "Stops preparation before any driver installation starts.", null,
                    service.Cancel), "gpu-updates.cancel:" + id));
            }

            stack.Children.Add(Card(card, "gpu-updates.card:" + id));
        }

        stack.Children.Add(ChoiceRow("Check automatically", new[] { (false, "Off"), (true, "Daily") },
            service.Automatic, service.SetAutomatic));
        if (service.History is { } history)
        {
            stack.Children.Add(Caption($"Previous driver: {history.PreviousVersion}. {history.Result}"));
        }

        stack.Children.Add(Caption(GpuDriverUpdateService.Rollback));
        SetContent(stack);
    }

    private void ConfirmInstall(string id, GpuDriverUpdateState state)
    {
        if (_service is not { } service || TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }

        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(new TextBlock { Text = GpuDriverUpdateService.InstallWarning
            + "\n\n" + state.Package!.Details, TextWrapping = TextWrapping.Wrap });
        var cancel = Command("gpu-update-confirm.cancel", "Cancel", () => window.Surfaces.CloseActive());
        var accept = Command("gpu-update-confirm.install", "Download and install", () =>
        {
            window.Surfaces.CloseActive();
            service.Install(id);
        });
        content.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { cancel, accept } });
        window.Surfaces.ShowConfirm(content, "Install " + state.Package.Version + "?", cancel);
    }

    private static ActionButton Keyed(ActionButton row, string key)
    {
        row.Tag = key;
        return row;
    }
}
