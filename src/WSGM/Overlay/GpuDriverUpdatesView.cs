using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Device > GPU's update workflow, sharing the session service with Steam's Display page.</summary>
public sealed class GpuDriverUpdatesView : ServiceSubView
{
    private IGpuDriverUpdateSource? _service;
    private OverlayProgress? _progress;
    private string? _notes;

    /// <inheritdoc />
    protected override string LogScope => "GPU driver updates";

    internal void Attach(IGpuDriverUpdateSource? service)
    {
        if (ReferenceEquals(_service, service))
        {
            return;
        }

        if (_service is not null)
        {
            _service.ProgressChanged -= OnProgressChanged;
        }
        _service = service;
        AttachSource(service);
        if (service is not null)
        {
            service.ProgressChanged += OnProgressChanged;
            Open();
        }
    }

    private void OnProgressChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_service is { } service && _progress is { } progress)
            {
                progress.Apply(ProgressState(service.Snapshot().FirstOrDefault(item => item.ShowsProgress), service));
            }
        }, DispatcherPriority.Background);
    }

    private static OverlayProgressState? ProgressState(GpuDriverUpdateState? item, IGpuDriverUpdateSource service)
    {
        return item is null ? null : new OverlayProgressState(item.StatusText, Active: true,
            item.Indeterminate ? null : item.Progress / 100d,
            item.CanCancel ? new OverlayProgressCancel("Cancel", "Stops preparation before installation.", service.Cancel) : null);
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

        var states = service.Snapshot();
        var busy = states.Any(item => item.Busy);
        stack.Children.Add(Tagged(Row("Check for driver updates", "Checks your GPU vendor's official catalog.",
            Icons.Restart, busy ? null : () => service.Check()), "gpu-updates.check"));
        if (states.Count == 0)
        {
            stack.Children.Add(Caption(service.DiscoveryDetail ?? GpuDriverUpdateState.EmptyText));
        }

        foreach (var item in states)
        {
            var id = item.Adapter.InstanceId;
            var card = new StackPanel { Spacing = 8, Tag = "gpu-updates.adapter:" + id };
            card.Children.Add(SectionLabel(item.Adapter.Name));
            card.Children.Add(Caption($"Installed: {item.Adapter.Version} · {item.Adapter.Date?.ToString("d") ?? "Date unknown"}"));
            if (item.ShowsProgress)
            {
                card.Children.Add(new OverlayProgress("gpu-updates.progress", ProgressState(item, service),
                    "gpu-updates.cancel:" + id));
            }
            else
            {
                card.Children.Add(Caption(item.StatusText));
            }

            if (item.CanInstall)
            {
                var refusal = service.InstallRefusal(item.Adapter);
                card.Children.Add(Tagged(PrimaryRow("Download and install", refusal ?? "Install the verified vendor package.",
                    Icons.Monitor, busy || refusal is not null ? null : () => ConfirmInstall(item)), "gpu-updates.install:" + id));
            }

            if (item.Package is { } package)
            {
                card.Children.Add(Caption($"Driver {package.Version} · {package.Certification}"));
                card.Children.Add(Tagged(Row("Release notes and license", package.Details.Host, Icons.Monitor, () =>
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

            if (item.CanCancel && !item.ShowsProgress)
            {
                card.Children.Add(Tagged(Row("Cancel", "Stops preparation before any driver installation starts.", null,
                    service.Cancel), "gpu-updates.cancel:" + id));
            }

            stack.Children.Add(Card(card, "gpu-updates.card:" + id));
        }

        stack.Children.Add(ChoiceRow("Check automatically", new[] { (false, "Off"), (true, "Daily") },
            service.Automatic, service.SetAutomatic));
        stack.Children.Add(ChoiceRow("Include non-WHQL (beta) drivers", new[] { (false, "Off"), (true, "On") },
            service.IncludeNonWhql, service.SetIncludeNonWhql));
        if (service.History is { } history)
        {
            stack.Children.Add(Caption($"Previous driver: {history.PreviousVersion}. {history.Result}"));
        }

        stack.Children.Add(Caption(GpuDriverUpdateService.Rollback));
        SetContent(stack);
        _progress = this.GetLogicalDescendants().OfType<OverlayProgress>().FirstOrDefault();
    }

    private void ConfirmInstall(GpuDriverUpdateState state)
    {
        if (_service is not { } service || state.Package is not { } package)
        {
            return;
        }
        ShowConfirm("Install " + package.Version + "?", GpuDriverUpdateService.InstallWarning + "\n\n" + package.Details,
            "Download and install", () =>
            {
                if (!service.Install(state.Adapter.InstanceId, package.Identity))
                {
                    Toast("The package changed or another driver operation is running. Check again before confirming.");
                }
            });
    }
}
