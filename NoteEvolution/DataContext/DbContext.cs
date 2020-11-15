using System;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Timers;
using DynamicData;
using LiteDB;
using NoteEvolution.Models;

namespace NoteEvolution.DataContext
{
    public class DbContext
    {
        private Timer _updateTimer;

        private LiteDatabase _db;

        private readonly ILiteCollection<Note> _dbNotes;
        private readonly ConcurrentDictionary<Guid, Note> _changedNotes;
        private readonly SourceCache<Note, Guid> _unsortedNoteListSource;
        public SourceCache<Note, Guid> UnsortedNoteListSource => _unsortedNoteListSource;

        public DbContext()
        {
            _updateTimer = new Timer(3000);
            _updateTimer.Elapsed += OnUpdateTimerElapsedEvent;
            _updateTimer.AutoReset = true;
            _updateTimer.Enabled = true;

            _db = new LiteDatabase("local.db");
            _changedNotes = new ConcurrentDictionary<Guid, Note>();
            _unsortedNoteListSource = new SourceCache<Note, Guid>(n => n.NoteId);
            _dbNotes = _db.GetCollection<Note>("notes");
            if (_dbNotes.Count() > 0)
                UnsortedNoteListSource.AddOrUpdate(_dbNotes.FindAll());

            _unsortedNoteListSource
                .Connect()
                .OnItemAdded(n => { if (_dbNotes.FindById(n.NoteId) == null) _dbNotes.Insert(n); })
                .DisposeMany()
                .Subscribe();
            _unsortedNoteListSource
                .Connect()
                .OnItemRemoved(n => { if (_dbNotes.FindById(n.NoteId) != null) _dbNotes.Delete(n.NoteId); })
                .DisposeMany()
                .Subscribe();
            _unsortedNoteListSource
               .Connect()
               .WhenPropertyChanged(n => n.Text, notifyOnInitialValue: false)
               .Do(n => { 
                   _changedNotes.TryAdd(n.Sender.NoteId, n.Sender);
                   _updateTimer.Interval = 3000;
               })
               .Subscribe();
        }

        private void OnUpdateTimerElapsedEvent(object sender, ElapsedEventArgs e)
        {
            if (_changedNotes.Count > 0)
            {
                _dbNotes.Update(_changedNotes.Values);
                _changedNotes.Clear();
            }
        }
    }
}
