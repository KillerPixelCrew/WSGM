using System;
using System.Linq;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private IProgramsSource? _programs;
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
            if (OpenToolsSection(OverlayPage.Programs))
            {
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
        QueueLiveRefresh(ProgramsLiveRefresh);
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
