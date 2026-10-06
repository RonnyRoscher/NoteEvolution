namespace NoteEvolution.Core.Links;

/// <summary>A completed operation that can be reversed.</summary>
public interface IUndoAction
{
    /// <summary>The operation's name, e.g. „Übernehmen“.</summary>
    string Description { get; }

    void Undo();
}

/// <summary>The undo stack of a vault session; keeps the 50 latest actions.</summary>
public sealed class UndoManager
{
    private const int Capacity = 50;

    private readonly LinkedList<IUndoAction> _actions = [];
    private readonly object _gate = new();

    /// <summary>Raised whenever the stack changes: an action was pushed or one was undone.</summary>
    public event Action? Changed;

    /// <summary>Raised after an action was pushed (and <see cref="Changed"/>), with that action; not for an undo.</summary>
    public event Action<IUndoAction>? Pushed;

    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _actions.Count > 0;
            }
        }
    }

    /// <summary>The <see cref="IUndoAction.Description"/> of the action <see cref="Undo"/> reverses next, or <c>null</c>.</summary>
    public string? NextDescription
    {
        get
        {
            lock (_gate)
            {
                return _actions.Last?.Value.Description;
            }
        }
    }

    public void Push(IUndoAction action)
    {
        lock (_gate)
        {
            _actions.AddLast(action);
            if (_actions.Count > Capacity)
            {
                _actions.RemoveFirst();
            }
        }

        Changed?.Invoke();
        Pushed?.Invoke(action);
    }

    /// <summary>
    /// Reverses the latest action; does nothing if there is none. The action leaves the stack before it runs,
    /// so one that fails (its exception is passed on) does not block the actions below it.
    /// </summary>
    public void Undo()
    {
        IUndoAction action;
        lock (_gate)
        {
            if (_actions.Last is not { } last)
            {
                return;
            }

            action = last.Value;
            _actions.RemoveLast();
        }

        try
        {
            action.Undo();
        }
        finally
        {
            Changed?.Invoke();
        }
    }
}
