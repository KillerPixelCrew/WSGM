using WSGM.Setup;
using WSGM.Setup.Engine;
using WSGM.Setup.UI;

namespace WSGM.Tests.Setup;

public sealed class SetupViewModelTests
{
    [Theory]
    [InlineData("progress")]
    [InlineData("loading")]
    [InlineData("maintain")]
    public void HiddenCommandsLeaveThePageAndFlowPositionUnchanged(string kind)
    {
        using SetupTestInstallation installation = new();
        var pageActions = 0;
        Page page = kind switch
        {
            "progress" => new ProgressPage("Installing", "Installing WSGM"),
            "loading" => new MessagePage("Profile", "Reading your settings…", "", ""),
            _ => new MaintainPage(installation.Engine.ThisVersion, () => pageActions++,
                () => pageActions++, () => pageActions++)
        };
        SetupViewModel model = new(new SetupOptions(), installation.Engine, page,
            ["profile", "customize", "progress", "summary"], 2);
        var closes = 0;
        var pageChanges = 0;
        model.CloseRequested += () => closes++;
        model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(SetupViewModel.Page))
            {
                pageChanges++;
            }
        };
        var hint = model.HintLeft;

        // The window's gamepad A fallback and B/Escape execute these exact commands.
        model.PrimaryCommand.Execute(null);
        model.BackCommand.Execute(null);
        model.PrimaryCommand.Execute(null);

        Assert.Same(page, model.Page);
        Assert.Equal(hint, model.HintLeft);
        Assert.Equal(0, pageChanges);
        Assert.Equal(0, closes);
        Assert.Equal(0, pageActions);
        Assert.Empty(installation.Runtime.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressPressesCannotSkipTheSummaryWhenTheEngineFinishes(bool registrationFails)
    {
        using SetupTestInstallation installation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new(false);
        installation.Runtime.OnStop = _ =>
        {
            entered.SetResult();
            release.Wait();
            return true;
        };
        installation.Runtime.OnRun = (_, arguments) => registrationFails && arguments == "--install" ? 1 : 0;
        SetupViewModel model = new(new SetupOptions(), installation.Engine,
            new ProgressPage("Installing", "Installing WSGM"),
            ["profile", "customize", "progress", "summary"], 2);
        var closes = 0;
        model.CloseRequested += () => closes++;
        var run = model.RunPlanAsync(installation.InstallThroughService(), false, action => action());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var progress = Assert.IsType<ProgressPage>(model.Page);
            Assert.Equal("Step 3 of 4", model.HintLeft);
            model.PrimaryCommand.Execute(null);
            model.BackCommand.Execute(null);
            Assert.Same(progress, model.Page);
            Assert.Equal("Step 3 of 4", model.HintLeft);
            Assert.False(model.RequestClose());
            Assert.Equal(0, closes);
        }
        finally
        {
            release.Set();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var summary = Assert.IsType<SummaryPage>(model.Page);
        Assert.Equal("Step 4 of 4", model.HintLeft);
        Assert.Equal(registrationFails ? "Done, with a problem" : "Done", summary.Eyebrow);
        Assert.Equal("Start WSGM", summary.Primary);
        Assert.True(model.RequestClose());
        Assert.Equal(registrationFails, summary.HasProblem);
        if (registrationFails)
        {
            Assert.Contains("The sign-in service could not be registered", summary.Problem, StringComparison.Ordinal);
        }

        // Summary Back is also hidden; the visible Primary still starts WSGM and closes.
        model.BackCommand.Execute(null);
        Assert.Same(summary, model.Page);
        model.PrimaryCommand.Execute(null);
        Assert.Equal(1, closes);
        Assert.Contains("Start --shell --activate", installation.Runtime.Calls);
    }

    [Fact]
    public async Task RefusedDataDeletionProducesAProblemSummaryWithoutClaimingTheDataWasDeleted()
    {
        using SetupTestInstallation installation = new(_ => false);
        SetupTestInstallation.Write(installation.User, "cache.bin", "kept");
        UninstallPage page = new("2.1.0", false, false) { KeepData = false };
        SetupViewModel model = new(new SetupOptions(), installation.Engine, page,
            ["uninstall", "progress", "summary"], 1);
        var deletion = installation.Engine.PlanUninstall(new UninstallChoices(false,
            false, false)).Last();

        await model.RunPlanAsync([deletion], true, action => action());

        var summary = Assert.IsType<SummaryPage>(model.Page);
        Assert.Equal("Uninstall finished with a problem", summary.Eyebrow);
        Assert.Equal("Some settings and data could not be deleted. See the details below.", summary.Lead);
        Assert.Equal(StepState.Failed, Assert.Single(summary.Steps).Step.State);
        Assert.Contains(Path.Combine(installation.User, "cache.bin"), summary.Problem, StringComparison.Ordinal);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(installation.User, "cache.bin")));
    }

    [Fact]
    public async Task RefusedProgramDeletionInTheUninstallPlanReportsTheRemainingFiles()
    {
        List<string> deleted = [];
        using SetupTestInstallation installation = new(path =>
        {
            deleted.Add(path);
            return false;
        });
        UninstallPage page = new("2.1.0", false, false);
        SetupViewModel model = new(new SetupOptions(), installation.Engine, page,
            ["uninstall", "progress", "summary"], 1);

        // Run the whole emitted plan: Windows registration, service, Steam and deletion operations are fakes.
        await model.RunPlanAsync(installation.Engine.PlanUninstall(new UninstallChoices(true,
                false, false)),
            true, action => action());

        var summary = Assert.IsType<SummaryPage>(model.Page);
        Assert.Equal("Uninstall finished with a problem", summary.Eyebrow);
        Assert.Equal("Some WSGM program files remain", summary.Title);
        var deletion = Assert.Single(summary.Steps, row => row.Step.Label == "Deleting program files");
        Assert.Equal(StepState.Failed, deletion.Step.State);
        Assert.Contains(Path.Combine(installation.Root, "App"), summary.Problem, StringComparison.Ordinal);
        Assert.Equal(new[] { "Plugins", "App", "App.previous", "App.staging", "Setup" }
            .Select(name => Path.Combine(installation.Root, name)), deleted);
        Assert.Contains("RemoveSetupRegistration", installation.Runtime.Calls);
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(installation.Root, "App", "WSGM.exe")));
    }

    [Fact]
    public async Task HiddenControllerSummaryAlsoReportsFailedDeletionAndKeepsTheRecoveryLedger()
    {
        using SetupTestInstallation installation = new(_ => false);
        const string ledger = """{"Deltas":[{"EntryKind":"Device","Value":"controller"}]}""";
        SetupTestInstallation.Write(installation.User, "hidhide-ownership.json", ledger);
        installation.Runtime.OnRun = (_, arguments) => arguments == "--uninstall-restore" ? 1 : 0;
        UninstallPage page = new("2.1.0", false, false) { KeepData = false };
        SetupViewModel model = new(new SetupOptions(), installation.Engine, page,
            ["uninstall", "progress", "summary"], 1);

        await model.RunPlanAsync(installation.Engine.PlanUninstall(new UninstallChoices(false,
                false, false)),
            true, action => action());

        var summary = Assert.IsType<SummaryPage>(model.Page);
        Assert.Equal("Your controller may still be hidden", summary.Title);
        Assert.Equal("Setup couldn't confirm that HidHide shows these devices again. Setup didn't retry.",
            summary.Lead);
        Assert.Contains("controller", summary.Problem, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(installation.Root, "App"), summary.Problem, StringComparison.Ordinal);
        Assert.Equal(ledger, File.ReadAllText(Path.Combine(installation.User, "hidhide-ownership.json")));
        Assert.Equal(1, installation.Runtime.Calls.Count(call => call == "Run --uninstall-restore"));
    }

    [Fact]
    public void VisibleMessageAndMaintainActionsKeepTheirExistingBehaviour()
    {
        using SetupTestInstallation installation = new();
        SetupViewModel message = new(new SetupOptions(), installation.Engine,
            new MessagePage("Setup", "Setup could not start", "Problem"), [], 0);
        var closes = 0;
        message.CloseRequested += () => closes++;
        message.PrimaryCommand.Execute(null);
        Assert.Equal(1, closes);

        List<string> actions = [];
        MaintainPage maintain = new(installation.Engine.ThisVersion,
            () => actions.Add("repair"), () => actions.Add("uninstall"), () => actions.Add("close"));
        maintain.Repair.Execute(null);
        maintain.Uninstall.Execute(null);
        maintain.Close.Execute(null);
        Assert.Equal(["repair", "uninstall", "close"], actions);
        Assert.Empty(installation.Runtime.Calls);
    }
}
