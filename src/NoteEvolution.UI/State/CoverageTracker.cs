using System.Data.Common;
using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.UI.State;

/// <summary>
/// The current book's coverage of a <see cref="VaultSession"/> (spec 6.1, 6.4): computed at most once per
/// <see cref="VaultSession.CoverageDelay"/> while an owner watches it (<see cref="Watch"/>). While nobody watches, a change
/// (<see cref="Schedule"/>) only marks it as out of date.
/// </summary>
internal sealed class CoverageTracker : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<Action, Task> _dispatch;
    private readonly IVault _vault;
    private readonly INoteRepository _notes;
    private readonly Func<string?> _focusBook;
    private readonly Func<UnderstandingParts?> _parts;
    private readonly Throttle _throttle;

    /// <param name="dispatch">Runs an action on the UI thread.</param>
    /// <param name="focusBook">The current book's link name; read on the UI thread.</param>
    /// <param name="parts">The language model's current parts (null while it does not run).</param>
    public CoverageTracker(TimeProvider time, ILogger logger, Func<Action, Task> dispatch, IVault vault, INoteRepository notes,
        Func<string?> focusBook, Func<UnderstandingParts?> parts)
    {
        _logger = logger;
        _dispatch = dispatch;
        _vault = vault;
        _notes = notes;
        _focusBook = focusBook;
        _parts = parts;
        _throttle = new Throttle(time, VaultSession.CoverageDelay, ComputeAsync, logger, "Computing the coverage");
    }

    // The owners and whether the coverage is out of date, under _gate.
    private readonly object _gate = new();
    private readonly HashSet<object> _watchers = new(ReferenceEqualityComparer.Instance);
    private bool _outdated = true;
    private bool _disposed;
    private int _computations;

    /// <summary><see cref="VaultSession.Coverage"/>; call it on the UI thread.</summary>
    public Coverage? Coverage { get; set; }

    /// <summary><see cref="VaultSession.CoverageChanged"/>; raised on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>How many coverage computations started; for tests.</summary>
    public int Computations => Volatile.Read(ref _computations);

    /// <summary>Called in the background just before the coverage is computed; for tests.</summary>
    public Action? Computing { get; set; }

    /// <summary>The coverage is being computed now; for tests.</summary>
    public bool Running => _throttle.Running;

    /// <summary><see cref="VaultSession.WatchCoverage"/>; call it on the UI thread.</summary>
    public void Watch(object owner, bool visible)
    {
        lock (_gate)
        {
            if (!(visible ? _watchers.Add(owner) : _watchers.Remove(owner)) || _disposed || !_outdated || _watchers.Count == 0)
            {
                return;
            }

            _throttle.Request();
        }
    }

    /// <summary>Something the coverage is computed from changed. Any thread; never throws.</summary>
    public void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _outdated = true;
            if (_watchers.Count > 0)
            {
                _throttle.Request();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _throttle.Dispose();
        }
    }

    private bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>
    /// Takes the current book and its notes on the UI thread, reads their placements and computes the coverage in the
    /// background, and applies it on the UI thread unless the book changed meanwhile (which computes it again). Only one
    /// computation runs at a time, so the last one applied is the newest. A failure is logged and the last coverage stays.
    /// </summary>
    private async Task ComputeAsync()
    {
        CoverageSource? source = null;
        Coverage coverage;
        try
        {
            await _dispatch(() => source = TakeSource());
            if (source is not { } taken)
            {
                return;
            }

            coverage = await Task.Run(() =>
            {
                Interlocked.Increment(ref _computations);
                Computing?.Invoke();

                // The store's placements are read here, not on the UI thread (a query or more per note). No check comes for
                // a note the background gave up, or one without topics to offer (with the map of this book).
                var understanding = taken.Parts?.Understanding;
                var sameBook = taken.Parts?.Placements.PlacementBook == taken.Book.LinkName;
                var withoutTopics = sameBook ? taken.Parts!.Placements.WithoutTopics : PlacementWork.NoKeys;
                var notes = taken.Notes
                    .Select(note => note.Used ? note : note with
                    {
                        Placement = understanding?.Placement(taken.Book, note.Key),
                        NoCheckComing = sameBook && (withoutTopics.Contains(note.Key)
                            || understanding?.PlacementStateOf(note.Key) == PlacementState.Failed),
                    })
                    .ToList();
                return CoverageCalculator.Compute(taken.Book, notes);
            });
        }
        catch (Exception ex)
        {
            // A closed or failing store ends the computation as well; it is not worth an error then.
            if (ex is not (ObjectDisposedException or DbException))
            {
                _logger.LogError(ex, "Computing the coverage failed");
            }

            lock (_gate)
            {
                _outdated = true;
            }

            return;
        }

        await _dispatch(() =>
        {
            if (!IsDisposed && string.Equals(_focusBook(), source.Book.LinkName, StringComparison.OrdinalIgnoreCase))
            {
                Coverage = coverage;
                Changed?.Invoke();
            }
        });
    }

    /// <summary>On the UI thread: what the coverage is computed from, or null if nobody watches or there is no book.</summary>
    private CoverageSource? TakeSource()
    {
        lock (_gate)
        {
            if (_disposed || _watchers.Count == 0)
            {
                return null; // still out of date: computed once someone watches
            }

            _outdated = false;
        }

        if ((_focusBook() is { } name ? _vault.FindBook(name) : null) is not { } book)
        {
            if (Coverage is not null)
            {
                Coverage = null;
                Changed?.Invoke();
            }

            return null;
        }

        // The placements are read in the background (see ComputeAsync).
        var coverageNotes = _notes.All()
            .Where(note => note.Block.Parent is null)
            .Select(note => new CoverageNote(note.Key, note.Date, note.IsUsed, note.Usages, null))
            .ToList();
        return new CoverageSource(book, _parts(), coverageNotes);
    }

    /// <summary>What the coverage is computed from: the current book, the language model's parts (if any) and the notes (without placements), taken on the UI thread.</summary>
    private sealed record CoverageSource(Book Book, UnderstandingParts? Parts, IReadOnlyList<CoverageNote> Notes);
}
