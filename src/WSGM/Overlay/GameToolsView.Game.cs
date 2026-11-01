using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class GameToolsView
{
    private readonly Dictionary<string, string> _draftChannel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _draftProxy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _draftSettings = new(StringComparer.Ordinal);
    private uint _appId;
    private string _gameName = "";

    /// <summary>Opens one game's page above the list and starts a scan of its folder.</summary>
    /// <param name="appId">Steam AppID or non-Steam shortcut id.</param>
    /// <param name="name">The library name, shown until the scan reports one; may be empty.</param>
    private void OpenGame(uint appId, string name)
    {
        ClearGame();
        _appId = appId;
        _gameName = name;
        Navigate(RenderGame);
        _ = RunSafelyAsync(ScanAsync(appId), "scan");
    }

    private void ClearGame()
    {
        _appId = 0;
        _gameName = "";
        _draftSettings.Clear();
        _draftProxy.Clear();
        _draftChannel.Clear();
    }

    private async Task ScanAsync(uint appId)
    {
        if (_service is not { } service)
        {
            return;
        }

        var generation = NavigationGeneration;
        await Task.Run(() => service.ReadAsync(appId));
        // The service raised Changed as well; this redraw covers a page hidden behind a surface meanwhile.
        if (generation == NavigationGeneration && appId == _appId)
        {
            CurrentLevel?.Invoke();
        }
    }

    private GameToolsState? CurrentState()
    {
        return _service is { } service && _appId != 0 ? service.Peek(_appId) : null;
    }

    private string GameName(GameToolsState state)
    {
        // A placeholder state carries the AppID as its name until the scan reports the real one.
        var placeholder = state.Loading && state.Name == state.AppId.ToString(CultureInfo.InvariantCulture);
        return state.Name.Length > 0 && !placeholder
            ? state.Name
            : _gameName.Length > 0 ? _gameName : state.Name;
    }
    

     rivate 
    oid 
    enderGame(
    )
    
    {
        if 
    C
    urrentState(
    )
 
    s 
    ot 
  
     
    tate)
    
        {
            RenderMessage(
    "
    Game Tools",
 
    Game Tools is not available in this session.")
    ;
    
            return;
    
        }

         ar 
    ame 
  
    ameName(
    s
    tate)
    ;
    
        var 
    ody 
  
    ewStack(
    "
    ")
    ;
    
        body.
    C
    hildren.
    A
    dd(
    T
    agged(
    C
    aption(
    "
    Game Tools  ›  " 
  
    ame)
    ,
 
    game.crumb")
    )
    ;
    
        if 
    s
    tate.
    E
    rror 
    s 
  
    ength:
 
     
     
     
    rror)
    
        {
            body.
    C
    hildren.
    A
    dd(
    B
    anner(
    "
    game.error",
 
    AInfoBarSeverity.
    E
    rror,
 
    ull,
 
    rror)
    )
    ;
    
        }
        else 
    f 
    s
    tate.
    N
    otice 
    s 
  
    ength:
 
     
     
     
    otice 
    & 
    tate.
    B
    usy 
    s 
    ull)
    
        {
            body.
    C
    hildren.
    A
    dd(
    B
    anner(
    "
    game.notice",
 
    AInfoBarSeverity.
    S
    uccess,
 
    ull,
 
    otice)
    )
    ;
    
        }

         f 
    s
    tate.
    A
    vailability 
    = 
    ready" 
    & 
    tate.
    R
    eason 
    s 
  
    ength:
 
     
     
     
    eason)
    
        {
            body.
    C
    hildren.
    A
    dd(
    B
    anner(
    "
    game.availability",
    
                state.
    A
    vailability 
    = 
    running" 
  
    AInfoBarSeverity.
    W
    arning 
  
    AInfoBarSeverity.
    I
    nformational,
    
                AvailabilityTitle(
    s
    tate.
    A
    vailability)
    ,
 
    eason)
    )
    ;
    
        }

         ody.
    C
    hildren.
    A
    dd(
    G
    ameCard(
    s
    tate,
 
    ame)
    )
    ;
    
        var 
    asFolder 
  
    tate.
    A
    vailability 
    s 
    ready" 
    r 
    running";
    
        var 
    ardProgress 
  
    alse;
    
        if 
    s
    tate.
    L
    oading 
    & 
    tate.
    T
    ools.
    C
    ount 
    = 
    )
    
        {
            body.
    C
    hildren.
    A
    dd(
    n
    ew 
    verlayProgress(
    "
    game.busy",
 
    rogressOf(
    s
    tate)
    )
    )
    ;
    
            cardProgress 
  
    rue;
    
        }
        else 
    f 
    h
    asFolder)
    
        {
            if 
    s
    tate.
    A
    ntiCheat.
    C
    ount 
  
    )
    
            {
                body.
    C
    hildren.
    A
    dd(
    B
    anner(
    "
    game.anticheat",
 
    AInfoBarSeverity.
    E
    rror,
 
    ntiCheatTitle(
    s
    tate)
    ,
    
                    AntiCheatMessage(
    s
    tate)
    )
    )
    ;
    
            }

             oreach 
    v
    ar 
    ool 
    n 
    tate.
    T
    ools)
    
            {
                cardProgress 
    = 
    tate.
    B
    usy?
    .
    T
    oolId 
    = 
    ool.
    I
    d;
    
                body.
    C
    hildren.
    A
    dd(
    T
    oolCard(
    s
    tate,
 
    ool,
 
    ame)
    )
    ;
    
            }
        }
        else
        {
            foreach 
    v
    ar 
    ool 
    n 
    tate.
    T
    ools)
    
            {
                body.
    C
    hildren.
    A
    dd(
    U
    navailableCard(
    t
    ool)
    )
    ;
    
            }
        }

         f 
    !
    c
    ardProgress)
    
        {
            // The level's one live progress element: the folder scan, or work for a tool this page doesn't draw.
            body.
    C
    hildren.
    A
    dd(
    n
    ew 
    verlayProgress(
    "
    game.busy",
 
    rogressOf(
    s
    tate)
    )
    )
    ;
    
        }

         ar 
    dle 
  
    tate.
    B
    usy 
    s 
    ull 
    & 
    s
    tate.
    L
    oading;
    
        body.
    C
    hildren.
    A
    dd(
    T
    agged(
    C
    ommandStrip(
    
            Command(
    "
    game.refresh",
 
    Scan the folder again",
 
    dle
                ? 
    )
 
    > 
    unTool(
    (
    s
    ervice,
 
    oken)
 
    > 
    ervice.
    R
    efreshAsync(
    _
    appId,
 
    oken)
    ,
 
    refresh")
    
                : 
    ull)
    ,
    
            Command(
    "
    game.back",
 
    Back to games",
 
    )
 
    > 
    ack(
    )
    )
    )
    ,
 
    game.commands")
    )
    ;
    
        SetGameContent(body, /// <summary>
    ///     Sets a level of the open game: download ticks update its progress element in place, and only a structural
    ///     change of the game's state redraws it.
    /// </summary>
    /// <param name="body">The level.</param>
    /// <param name="state">The state the level was drawn from.</param>
    /// <param name="progress">The level's progress element state, or null for a level without one.</param>or a level without one.</param>
    private void SetGameContent(StackPanel body, GameToolsState state,
        Func<GameToolsState, OverlayProgre/// <summary>
    ///     Whether two states of the game draw the same page apart from the running work's byte progress. A new
    ///     phase is structural, because the step indicator, the Cancel availability and the explanation follow it.
    /// </summary> structural, because the step indicator, the Cancel availability and the explanation follow it.
    /// </summary>
    private static bool SameStructure(GameToolsState shown, GameToolsState next)
    {
        // The cached scan's lists keep their identity between publications; Peek re-projects only the tools.
        return shown with { Busy = null, Tools = Array.Empty<GameToolsTool>() }
               == next with { Busy = null, Tools = Array.Empty<GameToolsTool>() }
               && (shown.Busy is null ? next.Busy is null : next.Busy is not null
                                                            && shown.Busy with { Bytes = 0, Total = 0 }
                                                            == next.Busy with { Bytes = 0, Total = 0 })
               && shown.Tools.SequenceEqual(next.Tools);
    }

    /// <summary>The level's progress element: the operation's phase line, amount and Cancel, or the folder scan.</summary>
    private OverlayProgressState? ProgressOf(
    G
    ameToolsState 
    tate)
    
    {
        if 
    s
    tate.
    B
    usy 
    s 
  
     
    usy)
    
        {
            var 
    mount 
  
    usy.
    B
    ytes 
    = 
    
                ? 
    "
                : 
    usy.
    T
    otal 
  
    
                    ? 
    " · {L
    ibraryCardView.
    F
    ormatBytes(
    b
    usy.
    B
    ytes)
    }
     of {L
    ibraryCardView.
    F
    ormatBytes(
    b
    usy.
    T
    otal)
    }
    "
                    : 
    " · {L
    ibraryCardView.
    F
    ormatBytes(
    b
    usy.
    B
    ytes)
    }
    ";
    
            var 
    ppId 
  
    tate.
    A
    ppId;
    
            return 
    ew 
    verlayProgressState(
    b
    usy.
    D
    etail 
  
    …" 
  
    mount,
 
    rue,
    
                busy.
    T
    otal 
  
     
     
    d
    ouble)
    b
    usy.
    B
    ytes 
  
    usy.
    T
    otal 
  
    ull,
    
                busy.
    C
    ancellable
                    ? 
    ew 
    verlayProgressCancel(
    "
    Cancel",
 
    ",
    
                        ()
 
    > 
    unTool(
    (
    s
    ervice,
 
    oken)
 
    > 
    ervice.
    C
    ancelAsync(
    a
    ppId,
 
    oken)
    ,
 
    cancel")
    )
    
                    : 
    ull)
    ;
    
        }

         eturn 
    tate.
    L
    oading 
  
    ew 
    verlayProgressState(
    "
    Scanning the game folder…", true) : null;
    }

    // ---- The game card ----

    private Border GameCard(GameToolsState state, string name)
    {
        // Once a tool is installed or working the card is one line, so the tool card leads the page.
        var compact = state.Busy is not null || state.Tools.Any(tool => tool.InstalledVersion is not null);
        var hasFolder = state.Availability is "ready" or "running";
        var change = hasFolder && state.Busy is null && !state.Loading
            ? Tagged(Command("game.change-program",
                state.Runtime is null ? "Choose the program…" : "Change program…",
                () => Navigate(RenderCandidates), state.Runtime is null), "game.change-program")
            : null;
        var title = new TextBlock
        {
            Text = name, FontSize = compact ? 20 : 22, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap
        };
        var heading = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(title);
        var folder = Caption(state.InstallFolder.Length > 0 ? state.InstallFolder : "Install folder not reported.");
        folder.TextWrapping = TextWrapping.NoWrap;
        folder.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(folder, state.InstallFolder);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 16, RowSpacing = 12
        };
        var wideFacts = !compact && _wide;
        grid.Children.Add(Capsule(state.AppId, compact || !_wide ? 120 : 184, compact || !_wide ? 56 : 86));
        if (compact)
        {
            heading.Children.Add(Caption(Summary(state)));
        }
        else
        {
            heading.Children.Add(folder);
        }

        if (wideFacts && hasFolder)
        {
            var main = new StackPanel { Spacing = 12 };
            main.Children.Add(heading);
            main.Children.Add(Facts(state));
            Grid.SetColumn(main, 1);
            grid.Children.Add(main);
        }
        else
        {
            Grid.SetColumn(heading, 1);
            grid.Children.Add(heading);
            if (!compact && hasFolder)
            {
                grid.RowDefinitions = new RowDefinitions("Auto,Auto");
                var facts = Facts(state);
                Grid.SetRow(facts, 1);
                Grid.SetColumnSpan(facts, 3);
                grid.Children.Add(facts);
            }
        }

        if (change is not null)
        {
            change.VerticalAlignment = wideFacts ? VerticalAlignment.Top : VerticalAlignment.Center;
            change.Margin = default;
            Grid.SetColumn(change, 2);
            grid.Children.Add(change);
        }

        return Card(grid, "game.card:" + compact + ":" + _wide);
    }

    private Grid 
    acts(
    G
    ameToolsState 
    tate)
    
    {
        var 
    acts 
  
    ew 
    rid 
  
    olumnDefinitions 
  
    ew 
    olumnDefinitions(
    "
    *,*,*")
    ,
 
    olumnSpacing 
  
    4 
    ;
    
        var 
    untime 
  
    tate.
    R
    untime;
    
        facts.
    C
    hildren.
    A
    dd(
    F
    act(
    "
    Program",
 
    untime?
    .
    F
    ileName 
    ? 
    s
    tate.
    L
    oading 
  
    Looking…" 
  
    Not chosen")
    ,
    
            runtime 
    s 
    ull
                ? 
    tate.
    R
    ememberedMissing
                    ? 
    The program chosen earlier is gone"
                    : 
    tate.
    L
    oading
                        ? 
    "
                        : 
    Several programs could be the game"
                : 
    tring.
    J
    oin(
    "
     · ",
 
    ew[
    ]
 
     
    untime.
    A
    rchitectureLabel,
 
    untime.
    C
    onfidenceLabel 
    
                    .W
    here(
    p
    art 
    > 
    art.
    L
    ength 
  
    )
    )
    ,
 
    ull)
    )
    ;
    
        var 
    pscalers 
  
    pscalerLabels(
    s
    tate)
    ;
    
        Control?
 
    hips 
  
    ull;
    
        if 
    u
    pscalers.
    C
    ount 
  
    )
    
        {
            var 
    rap 
  
    ew 
    rapPanel 
  
    rientation 
  
    rientation.
    H
    orizontal 
    ;
    
            foreach 
    v
    ar 
    abel 
    n 
    pscalers)
    
            {
                var 
    hip 
  
    hip(
    l
    abel,
 
    ")
    ;
    
                chip.
    M
    argin 
  
    ew 
    hickness(
    0
    ,
 
    ,
 
    ,
 
    )
    ;
    
                wrap.
    C
    hildren.
    A
    dd(
    c
    hip)
    ;
    
            }

             hips 
  
    rap;
    
        }

         ar 
    pscalerFact 
  
    act(
    "
    Upscalers in the folder",
 
    pscalers.
    C
    ount 
  
     
     
    ull 
  
    None found",
    
            upscalers.
    C
    ount 
  
     
     
    " 
  
    The game may still have one built in",
 
    hips)
    ;
    
        Grid.
    S
    etColumn(
    u
    pscalerFact,
 
    )
    ;
    
        facts.
    C
    hildren.
    A
    dd(
    u
    pscalerFact)
    ;
    
        var 
    ntiCheat 
  
    act(
    "
    Anti-cheat",
    
            state.
    A
    ntiCheat.
    C
    ount 
  
     
     
    tring.
    J
    oin(
    "
    , ",
 
    tate.
    A
    ntiCheat)
 
     
    None found",
    
            state.
    G
    puVendors.
    C
    ount 
  
     
     
    GPU: " 
  
    tring.
    J
    oin(
    "
    , ",
 
    tate.
    G
    puVendors.
    S
    elect(
    V
    endorLabel)
    )
 
     
    ",
 
    ull)
    ;
    
        Grid.
    S
    etColumn(
    a
    ntiCheat,
 
    )
    ;
    
        facts.
    C
    hildren.Add(antiCheat);
        return facts;
    }

    private static StackPanel Fact(string label, string? value, string detail, Control? content)
    {
        var fact = new StackPanel { Spacing = 3 };
        fact.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(), Classes = { "caption" }, FontSize = 11, FontWeight = FontWeight.SemiBold,
            LetterSpacing = 1, TextWrapping = TextWrapping.Wrap
        });
        if (value is not null)
        {
            fact.Children.Add(new TextBlock
                { Text = value, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        }

        if (content is not null)
        {
            fact.Children.Add(content);
        }

        if (detail.Length > 0)
        {
            var caption = Caption(detail);
            caption.Margin = default;
            fact.Children.Add(caption);
        }

        return fact;
    }

    /// <summary>The compact card's one line: program, how it was chosen, upscalers and anti-cheat.</summary>
    private static 
    tring 
    ummary(
    G
    ameToolsState 
    tate)
    
    {
        List<
    s
    tring>
 
    arts 
  
    ]
    ;
    
        if 
    s
    tate.
    R
    untime 
    s 
  
     
    untime)
    
        {
            parts.
    A
    dd(
    r
    untime.
    R
    elativePath.
    R
    eplace(
    '
    /',
 
    \\')
    )
    ;
    
            if 
    r
    untime.
    C
    onfidenceLabel.
    L
    ength 
  
    )
    
            {
                parts.
    A
    dd(
    c
    har.
    T
    oLowerInvariant(
    r
    untime.
    C
    onfidenceLabel[
    0
    ]
    )
 
     
    untime.
    C
    onfidenceLabel[
    1
    .
    .]
    )
    ;
    
            }
        }

         ar 
    pscalers 
  
    pscalerLabels(
    s
    tate)
    ;
    
        parts.
    A
    dd(
    u
    pscalers.
    C
    ount 
  
    
            ? 
    tring.
    J
    oin(
    "
    , ",
 
    pscalers)
 
     
     in the folder"
            : 
    no standalone upscaler found")
    ;
    
        parts.
    A
    dd(
    s
    tate.
    A
    ntiCheat.
    C
    ount 
  
     
     
    tring.
    J
    oin(
    "
    , ",
 
    tate.
    A
    ntiCheat)
 
     
     found" 
  
    no anti-cheat found")
    ;

       return string.Join(" · ", parts);
    }

    /// <summary>The distinct upscaler technologies, each with its major and minor version when the name has none.</summary>
    private static List<string>
 
    pscalerLabels(
    G
    ameToolsState 
    tate)
    
    {
        return
        [
            .. 
    tate.
    U
    pscalers.
    S
    elect(
    u
    pscaler 
    >
            {
                var 
    umbered 
  
    pscaler.
    T
    echnology.
    A
    ny(
    c
    har.
    I
    sDigit)
    ;
    
                var 
    arts 
  
    u
    pscaler.
    V
    ersion 
    ? 
    ")
    .
    S
    plit(
    '
    .')
    ;
    
                return 
    umbered 
    | 
    arts.
    L
    ength 
  
     
    | 
    arts[
    0
    ]
    .
    L
    ength 
    = 
    
                    ? 
    pscaler.
    T
    echnology
                    : 
    pscaler.
    T
    echnology 
  
     " 
  
    arts[
    0
    ]
 
     
    ." 
  
    arts[
    1
    ]
    ;
    
            })
    .
    Distinct(StringComparer.Ordinal)
        ];
    }

    private static string 
    ntiCheatTitle(
    G
    ameToolsState 
    tate)
    
    {
        return 
    tring.
    J
    oin(
    "
    , ",
 
    tate.
    A
    ntiCheat) + " found in this game's folder";
    }

    // The service's warning names what was found and then the tools' own warnings; the title already names it.
    private static string AntiCheatMessage(GameToolsState state)
    {
        var warning = state.AntiCheatWarning ?? "";
        var prefix = "Anti-cheat found: " + string.Join(", ", state.AntiCheat) + ".";
        return warning.StartsWith(prefix, StringComparison.Ordinal) ? warning[prefix.Length..].Trim() : warning;
    }

    private static string? AvailabilityTitle(string availability)
    {
        return availability switch
        {
            "running" => "The game is running",
            "folderUnavailable" => "Folder unavailable",
            "notInstalled" => "Not installed",
            "steamUnavailable" => "Steam unavailable",
            _ => null
        };
    }

    /// <summary>A bordered card around a panel, keyed so its editors survive publications.</summary>
    private static Border Card(Panel content, string tag)
    {
        return new Border { Child = content, Classes = { "device-group" }, Tag = tag };
    }

    private static string VendorLabel(string vendor)
    {
        return vendor switch
        {
            "amd" => "AMD",
            "intel" => "Intel",
            "nvidia" => "N
IIA",
            _ => vendor
     
   };
    }
}
