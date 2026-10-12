using SteamUiToolkit;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamCefActivationPolicyTests
{
    [Fact]
    public void ClosedListenerReceivesFullGraceFromItsFirstObservation()
    {
        var policy = new SteamCefActivationPolicy();
        var firstObservation = TimeSpan.FromMinutes(5);

        Assert.False(ObserveClosed(policy, firstObservation));
        Assert.False(ObserveClosed(policy,
            firstObservation + SteamCefActivationPolicy.StartupGrace - TimeSpan.FromTicks(1)));
        Assert.True(ObserveClosed(policy, firstObservation + SteamCefActivationPolicy.StartupGrace));
    }

    [Fact]
    public void FailedRecoveryCannotRepeatWhileListenerStaysClosed()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.False(ObserveClosed(policy, TimeSpan.Zero));
        Assert.True(ObserveClosed(policy, SteamCefActivationPolicy.StartupGrace));
        Assert.False(ObserveClosed(policy, SteamCefActivationPolicy.StartupGrace));
        Assert.False(ObserveClosed(policy, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void OpenListenerRearmsRecoveryForALaterFailure()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.False(ObserveClosed(policy, TimeSpan.Zero));
        Assert.True(ObserveClosed(policy, TimeSpan.FromSeconds(30)));
        Assert.False(policy.ShouldRecover(enabled: true, steamRunning: true, transitionPending: false,
            SteamCefDebuggingState.Open, TimeSpan.FromSeconds(31)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromSeconds(32)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromSeconds(61)));
        Assert.True(ObserveClosed(policy, TimeSpan.FromSeconds(62)));
    }

    [Theory]
    [InlineData(true, SteamCefDebuggingState.Closed)]
    [InlineData(false, SteamCefDebuggingState.Unavailable)]
    public void TransitionOrUnverifiedListenerRequiresANewContinuousGrace(bool transitionPending,
        SteamCefDebuggingState state)
    {
        var policy = new SteamCefActivationPolicy();

        Assert.False(ObserveClosed(policy, TimeSpan.Zero));
        Assert.False(policy.ShouldRecover(enabled: true, steamRunning: true, transitionPending,
            state, TimeSpan.FromSeconds(29)));
        Assert.False(policy.ShouldRecover(enabled: true, steamRunning: true, transitionPending,
            state, TimeSpan.FromMinutes(2)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromSeconds(121)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromSeconds(150)));
        Assert.True(ObserveClosed(policy, TimeSpan.FromSeconds(151)));
    }

    [Theory]
    [InlineData(true, SteamCefDebuggingState.Closed)]
    [InlineData(false, SteamCefDebuggingState.Unavailable)]
    public void TransitionOrUnverifiedListenerDoesNotRearmAFailedAttempt(bool transitionPending,
        SteamCefDebuggingState state)
    {
        var policy = new SteamCefActivationPolicy();

        Assert.False(ObserveClosed(policy, TimeSpan.Zero));
        Assert.True(ObserveClosed(policy, TimeSpan.FromSeconds(30)));
        Assert.False(policy.ShouldRecover(enabled: true, steamRunning: true, transitionPending,
            state, TimeSpan.FromSeconds(31)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromSeconds(32)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void DisabledIntegrationRearmsAfterANewGrace(bool enabled, bool steamRunning)
    {
        var policy = new SteamCefActivationPolicy();

        Assert.False(ObserveClosed(policy, TimeSpan.Zero));
        Assert.True(ObserveClosed(policy, TimeSpan.FromSeconds(30)));
        Assert.False(policy.ShouldRecover(enabled, steamRunning, transitionPending: false,
            SteamCefDebuggingState.Closed, TimeSpan.FromSeconds(31)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromSeconds(32)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromSeconds(61)));
        Assert.True(ObserveClosed(policy, TimeSpan.FromSeconds(62)));
    }

    [Fact]
    public void ReplacementSteamAbsenceDoesNotRearmAClaimedRecovery()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.False(ObserveClosed(policy, TimeSpan.Zero));
        Assert.True(ObserveClosed(policy, TimeSpan.FromSeconds(30)));
        Assert.False(policy.ShouldRecover(enabled: true, steamRunning: false, transitionPending: false,
            SteamCefDebuggingState.Closed, TimeSpan.FromSeconds(31)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromSeconds(32)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromHours(1)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void SteamAbsenceBeforeAClaimStartsANewStartupGrace()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.False(ObserveClosed(policy, TimeSpan.Zero));
        Assert.False(policy.ShouldRecover(enabled: true, steamRunning: false, transitionPending: false,
            SteamCefDebuggingState.Closed, TimeSpan.FromSeconds(29)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromSeconds(32)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromSeconds(61)));
        Assert.True(ObserveClosed(policy, TimeSpan.FromSeconds(62)));
    }

    [Fact]
    public void OpenListenerClearsAnAttemptEvenDuringATransition()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.False(ObserveClosed(policy, TimeSpan.Zero));
        Assert.True(ObserveClosed(policy, TimeSpan.FromSeconds(30)));
        Assert.False(policy.ShouldRecover(enabled: true, steamRunning: true, transitionPending: true,
            SteamCefDebuggingState.Open, TimeSpan.FromSeconds(31)));
        Assert.False(ObserveClosed(policy, TimeSpan.FromSeconds(32)));
        Assert.True(ObserveClosed(policy, TimeSpan.FromSeconds(62)));
    }

    private static bool ObserveClosed(SteamCefActivationPolicy policy, TimeSpan now)
    {
        return policy.ShouldRecover(enabled: true, steamRunning: true, transitionPending: false,
            SteamCefDebuggingState.Closed, now);
    }
}
