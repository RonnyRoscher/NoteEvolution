using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;

namespace NoteEvolution.ViewModels
{
    public class DocumentCollectionViewModel : ViewModelBase
    {
        private readonly SourceCache<Note, Guid> _globalNoteListSource;
        private readonly SourceCache<TextUnit, Guid> _globalTextUnitListSource;
        private readonly SourceCache<Document, Guid> _globalDocumentListSource;
        private readonly ReadOnlyObservableCollection<DocumentViewModel> _documentListView;
        private readonly SourceCache<ContentSource, Guid> _contentSourceListSource;

        public DocumentCollectionViewModel(SourceCache<Note, Guid> globalNoteListSource, SourceCache<TextUnit, Guid> globalTextUnitListSource, SourceCache<Document, Guid> globalDocumentListSource, SourceCache<ContentSource, Guid> contentSourceListSource)
        {
            _globalNoteListSource = globalNoteListSource;
            _globalTextUnitListSource = globalTextUnitListSource;
            _globalDocumentListSource = globalDocumentListSource;
            _contentSourceListSource = contentSourceListSource;

            UnsortedNotes = new SourceNotesViewModel(_globalNoteListSource);
            TextUnitProperties = new TextUnitPropertiesViewModel(_contentSourceListSource);

            // build sorted document list
            var documentComparer = SortExpressionComparer<DocumentViewModel>.Descending(d => d.Value.ModificationDate);
            var documentWasModified = _globalDocumentListSource
                .Connect()
                .WhenPropertyChanged(d => d.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _globalDocumentListSource
                .Connect()
                .Transform(d => new DocumentViewModel(d))
                .Sort(documentComparer, documentWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _documentListView)
                .DisposeMany()
                .Subscribe();
            _globalDocumentListSource.AddOrUpdate(new List<Document>());

            ChangedSelection = this
                .WhenPropertyChanged(d => d.SelectedItem)
                .Where(d => d.Value != null)
                .Select(d => d.Value);

            // set LastAddedDocument on new document added, used to auto focus the textbox
            _globalDocumentListSource
                .Connect()
                .OnItemAdded(d => LastAddedDocument = d)
                .DisposeMany()
                .Subscribe();

            CreateNewDocumentCommand = ReactiveCommand.Create(ExecuteCreateNewDocument);
            DissolveSelectedDocumentCommand = ReactiveCommand.Create(ExecuteDissolveSelectedDocument);
            DeleteSelectedDocumentCommand = ReactiveCommand.Create(ExecuteDeleteSelectedDocument);

            SelectDocument(_globalDocumentListSource.Items.OrderByDescending(n => n.ModificationDate).FirstOrDefault());
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> CreateNewDocumentCommand { get; }

        void ExecuteCreateNewDocument()
        {
            var newDocument = new Document(_globalNoteListSource, _globalTextUnitListSource, _contentSourceListSource);
            _globalDocumentListSource.AddOrUpdate(newDocument);
            SelectDocument(newDocument);
        }

        public ReactiveCommand<Unit, Unit> DissolveSelectedDocumentCommand { get; }

        void ExecuteDissolveSelectedDocument()
        {
            var closestItem = _globalDocumentListSource.Items.FirstOrDefault(note => note.ModificationDate > SelectedItem?.Value.ModificationDate);
            if (closestItem == null)
                closestItem = _globalDocumentListSource.Items.LastOrDefault(note => note.ModificationDate < SelectedItem?.Value.ModificationDate);
            var oldTextUnits = SelectedItem.Value.TextUnitList.ToList();
            foreach (var oldTextUnit in oldTextUnits)
                SelectedItem.Value.RemoveTextUnit(oldTextUnit);
            _globalDocumentListSource.Remove(SelectedItem.Value);
            SelectDocument(closestItem);
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedDocumentCommand { get; }

        void ExecuteDeleteSelectedDocument()
        {
            var closestItem = _globalDocumentListSource.Items.FirstOrDefault(note => note.ModificationDate > SelectedItem?.Value.ModificationDate);
            if (closestItem == null)
                closestItem = _globalDocumentListSource.Items.LastOrDefault(note => note.ModificationDate < SelectedItem?.Value.ModificationDate);
            var oldTextUnits = SelectedItem.Value.TextUnitList.ToList();
            foreach (var oldTextUnit in oldTextUnits)
                SelectedItem.Value.DeleteTextUnit(oldTextUnit);
            _globalDocumentListSource.Remove(SelectedItem.Value);
            SelectDocument(closestItem);
        }

        #endregion

        #region Public Methods

        public void SelectDocument(Document document)
        {
            if (document != null && SelectedItem?.Value.Id != document.Id)
            {
                SelectedItem = _documentListView.FirstOrDefault(d => d.Value.Id == document.Id);
            }
        }

        #endregion

        #region Public Properties

        private SourceNotesViewModel _unsortedNotes;

        public SourceNotesViewModel UnsortedNotes
        {
            get => _unsortedNotes;
            set => this.RaiseAndSetIfChanged(ref _unsortedNotes, value);
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

        private TextUnitPropertiesViewModel _textUnitProperties;

        public TextUnitPropertiesViewModel TextUnitProperties
        {
            get => _textUnitProperties;
            set => this.RaiseAndSetIfChanged(ref _textUnitProperties, value);
        }

        #endregion

        #region Public Observables

        public IObservable<DocumentViewModel> ChangedSelection { get; private set; }

        #endregion
    }
}
