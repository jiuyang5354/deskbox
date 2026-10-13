namespace DeskBox.Domain.StateMachines;

/// <summary>基于转移表的通用有限状态机，非法流转抛出 <see cref="InvalidTransitionException"/>。</summary>
public abstract class StateMachine<TState, TTrigger>
    where TState : struct, Enum where TTrigger : struct, Enum
{
    private readonly IReadOnlyDictionary<(TState, TTrigger), TState> _table;

    protected StateMachine(TState initial, IReadOnlyDictionary<(TState, TTrigger), TState> table)
    {
        State = initial;
        _table = table;
    }

    public TState State { get; private set; }

    public bool CanFire(TTrigger trigger) => _table.ContainsKey((State, trigger));

    public TState Fire(TTrigger trigger)
    {
        if (!_table.TryGetValue((State, trigger), out var next))
            throw new InvalidTransitionException(GetType().Name, State.ToString(), trigger.ToString());
        State = next;
        return next;
    }

    public static IEnumerable<(TState From, TTrigger Trigger, TState To)> Transitions(
        IReadOnlyDictionary<(TState, TTrigger), TState> table) =>
        table.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value));
}
