using System;
using DynamicData;
using ReactiveUI;
using NoteEvolution.Models;
using System.Linq;
using System.Reactive.Linq;

namespace NoteEvolution.ViewModels
{
    public class DocumentsViewModel : ViewModelBase
    {
        private SourceCache<Document, Guid> _documentListSource;
        private SourceCache<Note, Guid> _unsortedNoteListSource;

        public DocumentsViewModel(SourceCache<Note, Guid> unsortedNoteListSource, SourceCache<Document, Guid> documentListSource)
        {
            _unsortedNoteListSource = unsortedNoteListSource;
            _documentListSource = documentListSource;

            DocumentListView = new DocumentListViewModel(_unsortedNoteListSource, _documentListSource);
            // set LastAddedDocument on new document added, used to auto focus the textbox
            _documentListSource
                .Connect()
                .OnItemAdded(d => LastAddedDocument = d)
                .DisposeMany()
                .Subscribe();

            // set LastAddedText on new unsorted note added, used to auto focus the textbox
            DocumentListView
                .WhenAnyValue(dlv => dlv.SelectedItemContentTree)
                .Where(ct => ct != null)
                .Do(ct =>
                {
                    ct.GetDocumentNoteListSource()
                        .Connect()
                        .OnItemAdded(tu => LastAddedNote = tu.Content.FirstOrDefault())
                        .DisposeMany()
                        .Subscribe();
                })
                .Subscribe();
        }

        #region Public Properties


        private DocumentListViewModel _documentListView;

        public DocumentListViewModel DocumentListView
        {
            get => _documentListView;
            set => this.RaiseAndSetIfChanged(ref _documentListView, value);
        }

        private Document _lastAddedDocument;

        public Document LastAddedDocument
        {
            get => _lastAddedDocument;
            set => this.RaiseAndSetIfChanged(ref _lastAddedDocument, value);
        }

        private Note _lastAddedNote;

        public Note LastAddedNote
        {
            get => _lastAddedNote;
            set => this.RaiseAndSetIfChanged(ref _lastAddedNote, value);
        }

        #endregion
    }
}
