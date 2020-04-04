using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;
using System.Reflection;

namespace NoteEvolution.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private Document _unsortedNotesDocument;

        private SourceCache<Note, Guid> _unsortedNotesDocumentNoteListSource;
        private SourceCache<Note, Guid> _currentDocumentNoteListSource;

        public MainWindowViewModel()
        {
            CreateNewDocumentCommand = ReactiveCommand.Create(ExecuteCreateNewDocument);
            DeleteSelectedDocumentCommand = ReactiveCommand.Create(ExecuteDeleteSelectedDocument);
            DeleteSelectedDocumentAndNotesCommand = ReactiveCommand.Create(ExecuteDeleteSelectedDocumentAndNotes);
            LoadDocumentNotesCommand = ReactiveCommand.Create<Document>(ExecuteLoadDocumentNotes);
            SelectNoteCommand = ReactiveCommand.Create(ExecuteSelectNote);
            CreateNewNoteCommand = ReactiveCommand.Create(ExecuteCreateNewNote);
            CreateNewSuccessorNoteCommand = ReactiveCommand.Create(ExecuteCreateNewSuccessorNote);
            CreateNewChildNoteCommand = ReactiveCommand.Create(ExecuteCreateNewChildNote);
            DeleteSelectedNoteCommand = ReactiveCommand.Create(ExecuteDeleteSelectedNote);

            // build sorted document list
            _documentListSource = new SourceCache<Document, Guid>(d => d.DocumentId);
            var documentComparer = SortExpressionComparer<Document>.Descending(n => n.ModificationDate);
            var documentWasModified = _documentListSource
                .Connect()
                .WhenPropertyChanged(n => n.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _documentListSource
                .Connect()
                .Sort(documentComparer, documentWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _documentListView)
                .DisposeMany()
                .Subscribe();
            _unsortedNotesDocument = new Document { Title = "Unsorted Notes", DocumentId = Guid.Empty };
            _unsortedNotesDocumentNoteListSource = _unsortedNotesDocument.GetNoteListSource();
            _documentListSource.AddOrUpdate(new List<Document>{
                _unsortedNotesDocument
            });

            // load note list on document selection change
            this.WhenAnyValue(x => x.SelectedDocument)
                .Where(x => SelectedDocument != null)
                .InvokeCommand(LoadDocumentNotesCommand);

            SelectedDocument = _documentListSource.Items.LastOrDefault();

            ChangedSelection = this
                .WhenPropertyChanged(n => n.SelectedNote)
                .Where(nvm => nvm.Value != null)
                .Select(nvm => nvm.Value);
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> CreateNewDocumentCommand { get; }

        void ExecuteCreateNewDocument()
        {
            var newDocument = new Document();
            _documentListSource.AddOrUpdate(newDocument);
            SelectedDocument = newDocument;
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedDocumentCommand { get; }

        void ExecuteDeleteSelectedDocument()
        {
            if (SelectedDocument.DocumentId != _unsortedNotesDocument.DocumentId)
            {
                // move contained notes to unsorted document
                var hadAnyRemainingNotes = SelectedDocument.NoteList.Count() > 0;
                foreach (var note in SelectedDocument.NoteList)
                {
                    _unsortedNotesDocumentNoteListSource.AddOrUpdate(note);
                    note.RelatedDocument = _unsortedNotesDocument;
                }
                _currentDocumentNoteListSource.Clear();
                _documentListSource.Remove(SelectedDocument);
                if (hadAnyRemainingNotes)
                    SelectedDocument = _unsortedNotesDocument;
            }
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedDocumentAndNotesCommand { get; }

        void ExecuteDeleteSelectedDocumentAndNotes()
        {
            if (SelectedDocument.DocumentId != _unsortedNotesDocument.DocumentId)
            {
                _currentDocumentNoteListSource.Clear();
                var closestItem = _documentListSource.Items.FirstOrDefault(note => note.ModificationDate > SelectedDocument?.ModificationDate);
                if (closestItem == null)
                    closestItem = _documentListSource.Items.LastOrDefault(note => note.ModificationDate < SelectedDocument?.ModificationDate);
                ExecuteDeleteSelectedDocument();
                SelectedDocument = closestItem;
            }
        }

        public ReactiveCommand<Document, Unit> LoadDocumentNotesCommand { get; }

        void ExecuteLoadDocumentNotes(Document selectedDocument)
        {
            // build sorted note list
            _currentDocumentNoteListSource = SelectedDocument.GetNoteListSource();
            var noteComparer = SortExpressionComparer<Note>.Descending(note => note.ModificationDate);
            var noteWasModified = _currentDocumentNoteListSource
                .Connect()
                .WhenPropertyChanged(note => note.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _currentDocumentNoteListSource
                .Connect()
                .Sort(noteComparer, noteWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _noteListOutput)
                .DisposeMany()
                .Subscribe();
            NoteListView = _noteListOutput;
            DocumentRootNoteTreeView = new NoteTreeViewModel(selectedDocument.GetRootNoteListSource());

            DocumentRootNoteTreeView.ChangedSelection
                .Where(n => n.NoteId != SelectedNote.NoteId)
                .Do(n => SelectedNote = n)
                .SubscribeOn(RxApp.MainThreadScheduler)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe();
            /*ChangedSelection.
                Where(n => n.NoteId != DocumentRootNoteTreeView.SelectedItem?.Value?.NoteId)
                .Do(n => DocumentRootNoteTreeView.SelectedItem = DocumentRootNoteTreeView.Items.FirstOrDefault(nvm => nvm.Value.NoteId == n.NoteId))
                .SubscribeOn(RxApp.MainThreadScheduler)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe();*/

            SelectedNote = NoteListView.LastOrDefault();
        }
        
        public ReactiveCommand<Unit, Unit> SelectNoteCommand { get; }

        void ExecuteSelectNote()
        {
            var newNote = SelectedDocument.AddNote();
            if (newNote != null)
                SelectedNote = newNote;
        }


        public ReactiveCommand<Unit, Unit> CreateNewNoteCommand { get; }

        void ExecuteCreateNewNote()
        {
            var newNote = SelectedDocument.AddNote();
            if (newNote != null)
                SelectedNote = newNote;
        }

        public ReactiveCommand<Unit, Unit> CreateNewSuccessorNoteCommand { get; }

        void ExecuteCreateNewSuccessorNote()
        {
            var newNote = SelectedNote.AddSuccessor();
            if (newNote != null)
                SelectedNote = newNote;
        }

        public ReactiveCommand<Unit, Unit> CreateNewChildNoteCommand { get; }

        void ExecuteCreateNewChildNote()
        {
            var newNote = SelectedNote.AddChild();
            if (newNote != null)
                SelectedNote = newNote;
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedNoteCommand { get; }

        void ExecuteDeleteSelectedNote()
        {
            if (SelectedNote != null)
            {
                var closestItem = _currentDocumentNoteListSource.Items.FirstOrDefault(note => note.ModificationDate > SelectedNote.ModificationDate);
                if (closestItem == null)
                    closestItem = _currentDocumentNoteListSource.Items.LastOrDefault(note => note.ModificationDate < SelectedNote.ModificationDate);
                SelectedDocument.RemoveNote(SelectedNote);
                SelectedNote = (SelectedNote != closestItem) ? closestItem : null;
            }
        }

        #endregion

        #region Public Properties

        public string TitleBarText => "NoteEvolution v" + Assembly.GetEntryAssembly().GetName().Version;

        private SourceCache<Document, Guid> _documentListSource;

        private ReadOnlyObservableCollection<Document> _documentListView;

        public ReadOnlyObservableCollection<Document> DocumentListView => _documentListView;

        private NoteTreeViewModel _documentRootNoteTreeView;

        public NoteTreeViewModel DocumentRootNoteTreeView
        {
            get => _documentRootNoteTreeView;
            set => this.RaiseAndSetIfChanged(ref _documentRootNoteTreeView, value);
        }

        private Document _selectedDocument;

        public Document SelectedDocument
        {
            get => _selectedDocument;
            set => this.RaiseAndSetIfChanged(ref _selectedDocument, value);
        }

        private ReadOnlyObservableCollection<Note> _noteListOutput;

        private ReadOnlyObservableCollection<Note> _noteListView;

        public ReadOnlyObservableCollection<Note> NoteListView
        {
            get => _noteListView;
            set => this.RaiseAndSetIfChanged(ref _noteListView, value);
        }

        private Note _selectedNote;

        public Note SelectedNote
        {
            get => _selectedNote;
            set => this.RaiseAndSetIfChanged(ref _selectedNote, value);
        }

        #endregion

        #region Public Observables

        public IObservable<Note> ChangedSelection { get; private set; }

        #endregion
    }
}
