using LlamaLauncher.Domain;
using LlamaLauncher.Domain.StateMachine;
using Xunit;

namespace LlamaLauncher.UnitTests.Domain;

public sealed class InstanceStateMachineTests
{
    [Theory]
    [InlineData(InstanceState.Stopped, InstanceState.Starting)]
    [InlineData(InstanceState.Starting, InstanceState.LoadingModel)]
    [InlineData(InstanceState.Starting, InstanceState.Ready)]
    [InlineData(InstanceState.LoadingModel, InstanceState.Ready)]
    [InlineData(InstanceState.LoadingModel, InstanceState.Failed)]
    [InlineData(InstanceState.Ready, InstanceState.Stopping)]
    [InlineData(InstanceState.Stopping, InstanceState.Stopped)]
    [InlineData(InstanceState.Ready, InstanceState.ExitedUnexpectedly)]
    [InlineData(InstanceState.Failed, InstanceState.Starting)]
    [InlineData(InstanceState.ExitedUnexpectedly, InstanceState.Stopped)]
    public void CanTransition_AllowsExpectedTransitions(InstanceState from, InstanceState to)
        => Assert.True(InstanceStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(InstanceState.Stopped, InstanceState.Ready)]
    [InlineData(InstanceState.Stopped, InstanceState.LoadingModel)]
    [InlineData(InstanceState.Ready, InstanceState.Starting)]
    [InlineData(InstanceState.Stopping, InstanceState.Ready)]
    [InlineData(InstanceState.Ready, InstanceState.Stopped)]
    [InlineData(InstanceState.Failed, InstanceState.Ready)]
    public void CanTransition_RejectsIllegalTransitions(InstanceState from, InstanceState to)
        => Assert.False(InstanceStateMachine.CanTransition(from, to));

    [Fact]
    public void SameState_IsIdempotent()
        => Assert.True(InstanceStateMachine.CanTransition(InstanceState.Ready, InstanceState.Ready));

    [Fact]
    public void EnsureTransition_ThrowsWithDetails()
    {
        var exception = Assert.Throws<InvalidStateTransitionException>(
            () => InstanceStateMachine.EnsureTransition(InstanceState.Stopped, InstanceState.Ready));
        Assert.Equal(InstanceState.Stopped, exception.From);
        Assert.Equal(InstanceState.Ready, exception.To);
    }

    [Theory]
    [InlineData(InstanceState.Stopped, true)]
    [InlineData(InstanceState.Failed, true)]
    [InlineData(InstanceState.ExitedUnexpectedly, true)]
    [InlineData(InstanceState.Starting, false)]
    [InlineData(InstanceState.LoadingModel, false)]
    [InlineData(InstanceState.Ready, false)]
    [InlineData(InstanceState.Stopping, false)]
    public void IsTerminal_ClassifiesStates(InstanceState state, bool terminal)
        => Assert.Equal(terminal, InstanceStateMachine.IsTerminal(state));
}
