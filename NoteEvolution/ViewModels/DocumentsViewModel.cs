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
    public class DocumentsViewModel : ViewModelBase
    {
        private SourceCache<Note, Guid> _unsortedNoteListSource;
        private SourceCache<Document, Guid> _documentListSource;
        private ReadOnlyObservableCollection<Document> _documentListView;

        public DocumentsViewModel(SourceCache<Note, Guid> unsortedNoteListSource, SourceCache<Document, Guid> documentListSource)
        {
            CreateNewDocumentCommand = ReactiveCommand.Create(ExecuteCreateNewDocument);
            DissolveSelectedDocumentCommand = ReactiveCommand.Create(ExecuteDissolveSelectedDocument);
            DeleteSelectedDocumentCommand = ReactiveCommand.Create(ExecuteDeleteSelectedDocument);

            _unsortedNoteListSource = unsortedNoteListSource;
            _documentListSource = documentListSource;


            // build sorted document list
            var documentComparer = SortExpressionComparer<Document>.Descending(d => d.ModificationDate);
            var documentWasModified = _documentListSource
                .Connect()
                .WhenPropertyChanged(d => d.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _documentListSource
                .Connect()
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

            // load note list on document selection change
            ChangedSelection
                .Where(d => SelectedItem != null)
                .Do(d => {
                    SelectedItemContentTree = new TextUnitTreeViewModel(_unsortedNoteListSource, d.GetTextUnitListSource());
                    SelectedItemContentFlow = new TextUnitFlowViewModel(_unsortedNoteListSource, d.GetTextUnitListSource());
                })
                .Subscribe();

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
            SelectedItem = newDocument;
        }

        public ReactiveCommand<Unit, Unit> DissolveSelectedDocumentCommand { get; }

        void ExecuteDissolveSelectedDocument()
        {
            // move contained notes to unsorted notes
            if (SelectedItem.TextUnitList.Count() > 0)
            {
                foreach (var textUnit in SelectedItem.TextUnitList)
                {
                    foreach (var note in textUnit.Content)
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
            var closestItem = _documentListSource.Items.FirstOrDefault(note => note.ModificationDate > SelectedItem?.ModificationDate);
            if (closestItem == null)
                closestItem = _documentListSource.Items.LastOrDefault(note => note.ModificationDate < SelectedItem?.ModificationDate);
            _documentListSource.Remove(SelectedItem);
            SelectedItem = closestItem;
        }

        #endregion

        #region Public Methods

        public void SelectDocument(Document document)
        {
            if (document != null && SelectedItem?.DocumentId != document.DocumentId)
            {
                var newSelection = Items.FirstOrDefault(d => d.DocumentId == document.DocumentId);
                if (newSelection != null)
                    SelectedItem = newSelection;
            }
        }

        #endregion

        #region Public Properties

        public ReadOnlyObservableCollection<Document> Items => _documentListView;

        private Document _selectedItem;

        public Document SelectedItem
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

        private TextUnitTreeViewModel _selectedItemContentTree;

        public TextUnitTreeViewModel SelectedItemContentTree
        {
            get => _selectedItemContentTree;
            set => this.RaiseAndSetIfChanged(ref _selectedItemContentTree, value);
        }

        private TextUnitFlowViewModel _selectedItemContentFlow;

        public TextUnitFlowViewModel SelectedItemContentFlow
        {
            get => _selectedItemContentFlow;
            set => this.RaiseAndSetIfChanged(ref _selectedItemContentFlow, value);
        }

        #endregion

        #region Public Observables

        public IObservable<Document> ChangedSelection { get; private set; }

        #endregion
    }
}
