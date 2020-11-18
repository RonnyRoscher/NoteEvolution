using System;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Timers;
using DynamicData;
using LiteDB;
using PubSub;
using NoteEvolution.Models;
using NoteEvolution.Events;

namespace NoteEvolution.DataContext
{
    public class DbContext
    {
        private readonly Hub _eventAggregator;

        private Timer _updateTimer;
        private bool _isSaved;

        private LiteDatabase _db;

        private readonly ILiteCollection<Note> _dbNotes;
        private readonly ConcurrentDictionary<Guid, Note> _changedNotes;
        private readonly SourceCache<Note, Guid> _unsortedNoteListSource;
        public SourceCache<Note, Guid> UnsortedNoteListSource => _unsortedNoteListSource;

        public DbContext()
        {
            _eventAggregator = Hub.Default;

            _updateTimer = new Timer(3000);
            _updateTimer.Elapsed += OnUpdateTimerElapsedEvent;
            _updateTimer.AutoReset = true;
            _updateTimer.Enabled = true;

            _isSaved = true;

            _db = new LiteDatabase("local.db");
            _changedNotes = new ConcurrentDictionary<Guid, Note>();
            _unsortedNoteListSource = new SourceCache<Note, Guid>(n => n.NoteId);
            _dbNotes = _db.GetCollection<Note>("notes");
            if (_dbNotes.Count() > 0)
                UnsortedNoteListSource.AddOrUpdate(_dbNotes.FindAll());

            _unsortedNoteListSource
                .Connect()
                .OnItemAdded(n => { if (_dbNotes.FindById(n.NoteId) == null) _dbNotes.Insert(n); })
                .OnItemRemoved(n => { if (_dbNotes.FindById(n.NoteId) != null) _dbNotes.Delete(n.NoteId); })
                .DisposeMany()
                .Subscribe();
            _unsortedNoteListSource
               .Connect()
               .WhenAnyPropertyChanged(new[] { nameof(Note.Text) })
               .Do(n => { 
                   _changedNotes.TryAdd(n.NoteId, n);
                   _updateTimer.Interval = 3000;
                   if (_isSaved)
                   {
                       _isSaved = false;
                       _eventAggregator.Publish(new NotifySaveStateChanged(true));
                   }
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
            if (!_isSaved)
            {
                _isSaved = true;
                _eventAggregator.Publish(new NotifySaveStateChanged(false));
            }
        }
    }
}
