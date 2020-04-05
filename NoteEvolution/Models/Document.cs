using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using DynamicData;
using ReactiveUI;

namespace NoteEvolution.Models
{
    public class Document : ReactiveObject
    {
        public Document()
        {
            DocumentId = Guid.NewGuid();
            CreationDate = DateTime.Now;
            _noteListSource = new SourceCache<Note, Guid>(n => n.NoteId);
            _rootNoteListSource = new SourceCache<Note, Guid>(n => n.NoteId);

            var rootNote = new Note(this);
            _noteListSource.AddOrUpdate(rootNote);
            _rootNoteListSource.AddOrUpdate(rootNote);

            // update ModifiedDate on changes to local note properties
            this.WhenAnyValue(d => d.CreationDate, d => d.Title)
                .Select(_ => DateTime.Now)
                .ToProperty(this, d => d.ModificationDate, out _modificationDate);
            // update ModifiedDate on ModifiedDate changes in any associated note
            _noteListSource
                .Connect()
                .WhenPropertyChanged(n => n.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(n => n.Value)
                .ToProperty(this, d => d.ModificationDate, out _modificationDate);
        }

        #region Public Methods

        public SourceCache<Note, Guid> GetRootNoteListSource()
        {
            return _rootNoteListSource;
        }

        public SourceCache<Note, Guid> GetNoteListSource()
        {
            return _noteListSource;
        }

        public Note AddNote()
        {
            var latestRootNote = RootNoteList.LastOrDefault();
            if (latestRootNote != null)
                return latestRootNote.AddSuccessor();
            return null;
        }

        public void RemoveNote(Note oldNote)
        {
            if (oldNote != null)
                _noteListSource.Remove(oldNote);
        }

        #endregion

        #region Public Properties

        private Guid _documentId;

        public Guid DocumentId
        {
            get => _documentId;
            set => this.RaiseAndSetIfChanged(ref _documentId, value);
        }

        private DateTime _creationDate;

        public DateTime CreationDate
        {
            get => _creationDate;
            set => this.RaiseAndSetIfChanged(ref _creationDate, value);
        }

        readonly ObservableAsPropertyHelper<DateTime> _modificationDate;

        public DateTime ModificationDate => _modificationDate.Value;

        private string _title;

        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        public SourceCache<Note, Guid> _rootNoteListSource;

        public IEnumerable<Note> RootNoteList => _rootNoteListSource.Items;

        public SourceCache<Note, Guid> _noteListSource;

        public IEnumerable<Note> NoteList => _noteListSource.Items;

        #endregion
    }
}
