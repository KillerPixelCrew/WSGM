using System;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private IProgramsSource? _programs;
    private int _programRefreshQueued;
    internal event Action<string>? ProgramActivateRequested;

    internal void AttachPrograms(IProgramsSource? programs)
    {
        DetachPrograms();
        _programs = programs;
        ProgramsHost.Attach(programs);
        ProgramsHost.ActivateRequested += RequestProgramActivation;
        ProgramsHost.PinRequested += RequestProgramPin;
        ProgramsHost.UnpinRequested += RequestProgramUnpin;
        if (programs is not null)
        {
            programs.Changed += OnProgramsChanged;
        }

        RenderPins();
    }

    private void DetachPrograms()
    {
        if (_programs is not null)
        {
            _programs.Changed -= OnProgramsChanged;
        }

        ProgramsHost.ActivateRequested -= RequestProgramActivation;
        ProgramsHost.PinRequested -= RequestProgramPin;
        ProgramsHost.UnpinRequested -= RequestProgramUnpin;
        ProgramsHost.Attach(null);
        _programs = null;
    }

    internal void ReleaseProgramsPresentation()
    {
        DetachPrograms();
        ProgramsHost.Leave();
        foreach (var row in PinnedSectionsGrid.Children.OfType<ProgramActionRow>())
        {
            row.ReleaseIcon();
        }
    }

    private void RequestProgramActivation(string id)
    {
        var state = _programs?.ReadState().FirstOrDefault(item => item.Definition.Id == id);
        if (state is { CanLaunch: false, Running: false })
        {
            if (_navigation.Destination != OverlayDestination.System)
            {
                SelectDestination(OverlayDestination.System);
            }

            var section = _workspaceSections.FirstOrDefault(item => item.Page == OverlayPage.Programs);
            if (section is not null)
            {
                SelectWorkspaceSection(section, false);
                ProgramsHost.OpenEditor(id);
                FocusFirstControl(ProgramsHost);
            }

            return;
        }

        ProgramActivateRequested?.Invoke(id);
    }

    private void RequestProgramPin(string id) => PinToggleRequested?.Invoke(ProgramsService.PinPrefix + id);

    private void RequestProgramUnpin(string id)
    {
        var pin = ProgramsService.PinPrefix + id;
        if (_pins.Contains(pin))
        {
            // Queue the final removal after any older home-pin writes already admitted by the controller.
            PinToggleRequested?.Invoke(pin);
        }
    }

    private void OnProgramsChanged()
    {
        if (Interlocked.Exchange(ref _programRefreshQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _programRefreshQueued, 0);
            if (!_closed)
            {
                RenderPins();
            }
        }, DispatcherPriority.Background);
    }

    private Control CreateProgramPin(string pin)
    {
        var id = pin[ProgramsService.PinPrefix.Length..];
        var state = _programs?.ReadState().FirstOrDefault(item => item.Definition.Id == id);
        if (state is null)
        {
            return new ActionButton
            {
                Tag = PinTagPrefix + pin,
                Title = "Program unavailable",
                Description = "X, touch hold or right-click to unpin",
                IconGeometry = Icons.Rocket
            };
        }

        var existing = PinnedSectionsGrid.Children.OfType<ProgramActionRow>()
            .FirstOrDefault(row => Equals(row.Tag, PinTagPrefix + pin));
        if (existing is not null)
        {
            existing.Apply(state);
            return existing;
        }

        return new ProgramActionRow(state, () => RequestProgramActivation(id)) { Tag = PinTagPrefix + pin };
    }
}
