using System;
using System.Reactive.Linq;
using DynamicData;
using LiteDB;
using ReactiveUI;
using NoteEvolution.Models;

namespace NoteEvolution.DataContext
{
    public class DbContext
    {
        private LiteDatabase _db;

        private readonly ILiteCollection<Note> _dbNotes;
        private readonly SourceCache<Note, Guid> _unsortedNoteListSource;
        public SourceCache<Note, Guid> UnsortedNoteListSource => _unsortedNoteListSource; 

        public DbContext()
        {
            _db = new LiteDatabase("local.db");

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
               .Throttle(TimeSpan.FromSeconds(3.0), RxApp.TaskpoolScheduler)
               .Do(n => { _dbNotes.Update(n.Sender); })
               .Subscribe();
        }
    }
}
