using System;
using System.Reactive.Linq;
using ReactiveUI;

namespace NoteEvolution.Models
{
    public class Note : ReactiveObject
    {
        public Note()
        {
            NoteId = Guid.NewGuid();
            CreationDate = DateTime.Now;

            this.WhenAnyValue(x => x.CreationDate, x => x.Text, x => x.RelatedDocument)
                .Select(_ => DateTime.Now)
                .ToProperty(this, x => x.ModificationDate, out _modificationDate);
        }

        private Guid _noteId;

        public Guid NoteId
        {
            get => _noteId;
            set => this.RaiseAndSetIfChanged(ref _noteId, value);
        }

        private DateTime _creationDate;

        public DateTime CreationDate
        {
            get => _creationDate;
            set => this.RaiseAndSetIfChanged(ref _creationDate, value);
        }

        readonly ObservableAsPropertyHelper<DateTime> _modificationDate;
        public DateTime ModificationDate => _modificationDate.Value;

        private Document _relatedDocument;

        public Document RelatedDocument
        {
            get => _relatedDocument;
            set => this.RaiseAndSetIfChanged(ref _relatedDocument, value);
        }

        private string _text;

        public string Text
        {
            get => _text;
            set => this.RaiseAndSetIfChanged(ref _text, value);
        }
    }
}
