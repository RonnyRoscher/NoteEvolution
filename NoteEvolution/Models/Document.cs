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

            _noteListSource = new SourceCache<Note, Guid>(note => note.NoteId);

            this.WhenAnyValue(x => x.CreationDate, x => x.Title)
                .Select(_ => DateTime.Now)
                .ToProperty(this, x => x.ModificationDate, out _modificationDate);

            _noteListSource.Connect()
                .WhenPropertyChanged(n => n.Text)
                .Select(_ => DateTime.Now)
                .ToProperty(this, x => x.ModificationDate, out _modificationDate);
        }

        #region Public Methods

        public SourceCache<Note, Guid> GetNoteListSource()
        {
            return _noteListSource;
        }

        public void AddOrUpdateNote(Note newNote)
        {
            if (newNote != null)
                _noteListSource.AddOrUpdate(newNote);
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

        public SourceCache<Note, Guid> _noteListSource;

        public IEnumerable<Note> NoteList => _noteListSource.Items;

        #endregion
    }
}
