namespace Insys.BuildingBlocks.Application.Messaging;

/// <summary>
/// Work that must happen only once the command's transaction has committed, such as evicting a cache entry. It runs
/// in the same process; other processes learn of the change through the command's events.
/// </summary>
public interface IAfterCommit
{
    void Enqueue(Action action);
}

public sealed class AfterCommitActions : IAfterCommit
{
    private readonly List<Action> _actions = [];

    public void Enqueue(Action action) => _actions.Add(action);

    public void RunAll()
    {
        foreach (var action in _actions)
        {
            action();
        }

        _actions.Clear();
    }

    public void Discard() => _actions.Clear();
}
