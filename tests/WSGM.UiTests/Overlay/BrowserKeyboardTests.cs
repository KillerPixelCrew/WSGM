using System.IO.Pipes;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Browser;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class BrowserKeyboardTests
{
    [AvaloniaFact]
    public void WebsiteEnterDoesNotActivateTheLastOverlayButton()
    {
        using var fixture = new UiFixture();
        using var session = Session(fixture);
        var window = Open(fixture, session);
        var button = UiFixture.Named<BrowserView>(window, "BrowserHost").GetVisualDescendants().OfType<Button>()
            .Single(candidate => Equals(candidate.Content, "Keyboard"));
        var activations = 0;
        button.Click += (_, _) => activations++;
        button.Focus();
        Receive(session, new BrowserPacket { Kind = "viewport" });

        UiFixture.Key(window, Key.Enter);

        Assert.Equal(0, activations);
        Assert.False(window.HasActiveSurface);
        Assert.True(session.ViewportFocused);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WebsiteTextUsesTheLocalKeyboardAndReturnsToItsField(bool sensitive, bool multiline)
    {
        using var fixture = new UiFixture();
        using var session = Session(fixture);
        var window = Open(fixture, session);
        var externalRequests = 0;
        window.OnScreenKeyboardRequested += () => externalRequests++;
        var name = "WSGM.Tests.Browser." + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using NamedPipeClientStream client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await connected;
        typeof(BrowserSession).GetField("_pipe", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, server);
        Receive(session, new BrowserPacket
        {
            Kind = "keyboard", Visible = true, Sensitive = sensitive, Multiline = multiline,
            Text = "existing value", Replace = true, SelectionStart = 0, SelectionEnd = 8
        });
        Dispatcher.UIThread.RunJobs();

        var keyboard = Assert.Single(window.GetVisualDescendants().OfType<KeyboardPanel>());
        var input = UiFixture.Named<TextBox>(keyboard, "Input");
        Assert.Equal(sensitive ? '●' : '\0', input.PasswordChar);
        Assert.Equal(multiline, input.AcceptsReturn);
        Assert.Equal("existing value", input.Text);
        Assert.Equal(0, input.SelectionStart);
        Assert.Equal(8, input.SelectionEnd);
        Assert.Equal(0, externalRequests);
        Assert.True(keyboard.Bounds.Height <= (multiline ? 284 : 240));
        input.Text = multiline ? "first\nsecond" : "typed into the website";
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SurfaceClosed += () => closed.TrySetResult();
        if (multiline)
        {
            UiFixture.Click(window, UiFixture.Named<Button>(keyboard, "AcceptButton"));
        }
        else
        {
            UiFixture.Click(window, keyboard.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, Key.Enter)));
        }

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        BrowserPacket packet;
        do
        {
            packet = await BrowserWire.ReadAsync(client, timeout.Token);
        } while (packet.Kind != "text");
        Assert.Equal(input.Text, packet.Text);
        Assert.Equal(!multiline, packet.Submit);
        Assert.True(packet.Replace);
        Assert.False(window.HasActiveSurface);
        Assert.Equal(0, externalRequests);
    }

    private static BrowserSession Session(UiFixture fixture)
    {
        return new BrowserSession(fixture.Store.Context, new BrowserOptions(), options => options.Normalize(), () => null);
    }

    private static OverlayWindow Open(UiFixture fixture, BrowserSession session)
    {
        var window = fixture.Overlay(980, 640, session: new OverlayWindow.SessionState { Destination = OverlayDestination.System });
        window.AttachBrowserSession(session);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemBrowser));
        Receive(session, new BrowserPacket { Kind = "state" });
        Receive(session, new BrowserPacket { Kind = "address", Text = "https://example.test/" });
        // Headless fixtures never create native HWNDs. The authenticated packet routing still needs
        // a selected viewport, without constructing a helper or touching the desktop.
        typeof(BrowserSession).GetField("_viewport", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, (nint)1);
        return window;
    }

    private static void Receive(BrowserSession session, BrowserPacket packet)
    {
        typeof(BrowserSession).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [packet]);
    }
}
