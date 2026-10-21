namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// An aggregate's allowed transitions, copied from its stateTransitions list in domain-model.json.
/// Any other transition fails with INVALID_STATE_TRANSITION (409).
/// </summary>
public sealed class StateMachine<TState, TTrigger>
    where TState : struct, Enum
    where TTrigger : struct, Enum
{
    private readonly Dictionary<(TState From, TTrigger Trigger), TState> _transitions = [];

    public StateMachine<TState, TTrigger> Allow(TState from, TTrigger trigger, TState to)
    {
        _transitions.Add((from, trigger), to);
        return this;
    }

    public IEnumerable<(TState From, TTrigger Trigger, TState To)> Transitions =>
        _transitions.Select(t => (t.Key.From, t.Key.Trigger, t.Value));

    public bool CanFire(TState from, TTrigger trigger) => _transitions.ContainsKey((from, trigger));

    public Result<TState> Fire(TState from, TTrigger trigger) =>
        _transitions.TryGetValue((from, trigger), out var to)
            ? to
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"{trigger} is not allowed when the state is {from}.");
}
