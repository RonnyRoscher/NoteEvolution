using System;
using DynamicData;
using ReactiveUI;
using NoteEvolution.Models;
using System.Linq;
using System.Reactive.Linq;
using System.Collections.ObjectModel;
using System.Reactive;
using DynamicData.Binding;
using System.Collections.Generic;

namespace NoteEvolution.ViewModels
{
    public class DocumentCollectionViewModel : ViewModelBase
    {
        private readonly SourceCache<Note, Guid> _unsortedNoteListSource;
        private readonly SourceCache<Document, Guid> _documentListSource;
        private readonly ReadOnlyObservableCollection<DocumentViewModel> _documentListView;

        public DocumentCollectionViewModel(SourceCache<Note, Guid> unsortedNoteListSource, SourceCache<Document, Guid> documentListSource)
        {
            CreateNewDocumentCommand = ReactiveCommand.Create(ExecuteCreateNewDocument);
            DissolveSelectedDocumentCommand = ReactiveCommand.Create(ExecuteDissolveSelectedDocument);
            DeleteSelectedDocumentCommand = ReactiveCommand.Create(ExecuteDeleteSelectedDocument);

            _unsortedNoteListSource = unsortedNoteListSource;
            UnsortedNotesView = new SourceNotesViewModel(_unsortedNoteListSource);

            _documentListSource = documentListSource;

            // build sorted document list
            var documentComparer = SortExpressionComparer<DocumentViewModel>.Descending(d => d.DocumentSource.ModificationDate);
            var documentWasModified = _documentListSource
                .Connect()
                .WhenPropertyChanged(d => d.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _documentListSource
                .Connect()
                .Transform(d => new DocumentViewModel(_unsortedNoteListSource, d))
                .Sort(documentComparer, documentWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _documentListView)
                .DisposeMany()
                .Subscribe();
            _documentListSource.AddOrUpdate(new List<Document>());

            ChangedSelection = this
                .WhenPropertyChanged(d => d.SelectedItem)
                .Where(d => d.Value != null)
                .Select(d => d.Value);

            // set LastAddedDocument on new document added, used to auto focus the textbox
            _documentListSource
                .Connect()
                .OnItemAdded(d => LastAddedDocument = d)
                .DisposeMany()
                .Subscribe();
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> CreateNewDocumentCommand { get; }

        void ExecuteCreateNewDocument()
        {
            var newDocument = new Document();
            _documentListSource.AddOrUpdate(newDocument);
            SelectDocument(newDocument);
        }

        public ReactiveCommand<Unit, Unit> DissolveSelectedDocumentCommand { get; }

        void ExecuteDissolveSelectedDocument()
        {
            // move contained notes to unsorted notes
            if (SelectedItem.DocumentSource.TextUnitList.Count() > 0)
            {
                foreach (var textUnit in SelectedItem.DocumentSource.TextUnitList)
                {
                    foreach (var note in textUnit.NoteList)
                    {
                        _unsortedNoteListSource.AddOrUpdate(note);
                    }
                }
            }
            ExecuteDeleteSelectedDocument();
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedDocumentCommand { get; }

        void ExecuteDeleteSelectedDocument()
        {
            var closestItem = _documentListSource.Items.FirstOrDefault(note => note.ModificationDate > SelectedItem?.DocumentSource.ModificationDate);
            if (closestItem == null)
                closestItem = _documentListSource.Items.LastOrDefault(note => note.ModificationDate < SelectedItem?.DocumentSource.ModificationDate);
            _documentListSource.Remove(SelectedItem.DocumentSource);
            SelectDocument(closestItem);
        }

        #endregion

        #region Public Methods

        public void SelectDocument(Document document)
        {
            if (document != null && SelectedItem?.DocumentSource.DocumentId != document.DocumentId)
            {
                SelectedItem = _documentListView.FirstOrDefault(d => d.DocumentSource.DocumentId == document.DocumentId);
            }
        }

        #endregion

        #region Public Properties

        private SourceNotesViewModel _unsortedNotesView;

        public SourceNotesViewModel UnsortedNotesView
        {
            get => _unsortedNotesView;
            set => this.RaiseAndSetIfChanged(ref _unsortedNotesView, value);
        }

        public ReadOnlyObservableCollection<DocumentViewModel> Items => _documentListView;

        private DocumentViewModel _selectedItem;

        public DocumentViewModel SelectedItem
        {
            get => _selectedItem;
            set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
        }

        private Document _lastAddedDocument;

        public Document LastAddedDocument
        {
            get => _lastAddedDocument;
            set => this.RaiseAndSetIfChanged(ref _lastAddedDocument, value);
        }

        #endregion

        #region Public Observables

        public IObservable<DocumentViewModel> ChangedSelection { get; private set; }

        #endregion
    }
}
