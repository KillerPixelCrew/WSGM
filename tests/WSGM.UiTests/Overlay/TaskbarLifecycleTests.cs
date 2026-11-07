using Avalonia.Headless.XUnit;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

/// <summary>
///     The taskbar's open, re-show during the close grace and disposal through the controller's shared lifecycle.
///     The grace timer itself does not run on the headless dispatcher.
/// </summary>
public sealed class TaskbarLifecycleTests
{
    [AvaloniaFact]
    public void OpeningTwiceKeepsOneTaskbarAndOneCaptureClaim()
    {
        using var context = new Context();

        context.Controller.ShowTaskbar();
        context.Controller.ShowTaskbar();

        Assert.True(context.Controller.TaskbarOpen);
        Assert.Equal(["taskbar"], context.Opened);
        Assert.Empty(context.Closed);
    }

    [AvaloniaFact]
    public void ReShowingDuringTheGraceKeepsTheTaskbarAndItsClaim()
    {
        using var context = new Context();
        context.Controller.ShowTaskbar();
        // Starts the deferred close; the window stays for the touch grace.
        context.Controller.ToggleTaskbar();
        Assert.True(context.Controller.TaskbarOpen);

        context.Controller.ShowTaskbar();

        Assert.True(context.Controller.TaskbarOpen);
        Assert.Equal(["taskbar"], context.Opened);
        Assert.Empty(context.Closed);
    }

    [AvaloniaFact]
    public void DisposingDuringTheGraceClosesTheTaskbarAtOnceAndReleasesOnce()
    {
        var context = new Context();
        context.Controller.ShowTaskbar();
        context.Controller.ToggleTaskbar();

        context.Dispose();

        Assert.False(context.Controller.TaskbarOpen);
        Assert.Equal(["taskbar"], context.Opened);
        Assert.Equal(["taskbar"], context.Closed);
    }

    [AvaloniaFact]
    public void ADisplayDriverRestartClosesTheOpenSurfaceOnce()
    {
        using var context = new Context();
        context.Controller.ShowTaskbar();

        Assert.True(context.Controller.CloseAfterDisplayRestart());
        // The close is in its grace; a second finished install must not start another.
        Assert.False(context.Controller.CloseAfterDisplayRestart());
        // The next summon builds the surface again rather than keeping the closing one.
        context.Controller.ShowTaskbar();
        Assert.True(context.Controller.TaskbarOpen);
    }

    [AvaloniaFact]
    public void ADisplayDriverRestartLeavesAClosedOverlayAlone()
    {
        using var context = new Context();

        Assert.False(context.Controller.CloseAfterDisplayRestart());
        Assert.False(context.Controller.TaskbarOpen);
        Assert.Empty(context.Opened);
        Assert.Empty(context.Closed);
    }

    /// <summary>A controller without a Steam Input lease, recording its capture claims.</summary>
    private sealed class Context : IDisposable
    {
        private readonly AudioManager _audio = new();
        private readonly RemovableDriveManager _drives = new();
        private readonly UiFixture _fixture = new();
        private readonly RadioManager _radios = new();
        private bool _disposed;

        internal Context()
        {
            _fixture.Store.Update(_ => true);
            var config = new AppConfig { SteamInputLeaseEnabled = false };
            var blocker = new SteamInputBlocker(new SteamInputShim(),
                () => throw new InvalidOperationException("The taskbar test runs without a lease."));
            Controller = new OverlayController(config, _fixture.Store, blocker, null, new SessionModes(config, null),
                _audio, _radios, _drives);
            Controller.UiSurfaceOpened += Opened.Add;
            Controller.UiSurfaceClosed += Closed.Add;
        }

        internal OverlayController Controller { get; }
        internal List<string> Opened { get; } = [];
        internal List<string> Closed { get; } = [];

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Controller.Dispose();
            _audio.Dispose();
            _radios.Dispose();
            _drives.Dispose();
            _fixture.Dispose();
        }
    }
}
