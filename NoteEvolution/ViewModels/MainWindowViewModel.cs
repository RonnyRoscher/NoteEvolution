using System;
using System.Reactive;
using System.Reflection;
using DynamicData;
using PubSub;
using ReactiveUI;
using NoteEvolution.DataContext;
using NoteEvolution.Models;
using NoteEvolution.Events;

namespace NoteEvolution.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly string _titleBarTextBase;

        private readonly Hub _eventAggregator;

        private readonly DbContext _localDB;

        private readonly SourceCache<Note, Guid> _unsortedNoteListSource;
        private readonly SourceCache<Document, Guid> _documentListSource;

        public MainWindowViewModel()
        {
            _titleBarTextBase = "NoteEvolution v" + Assembly.GetEntryAssembly().GetName().Version + " ";
            TitleBarText = _titleBarTextBase;

            _eventAggregator = Hub.Default;
            _eventAggregator.Subscribe<NotifySaveStateChanged>(this, saveStateChange => { TitleBarText = _titleBarTextBase + (saveStateChange.HasUnsavedChanged ? "*" : ""); });

            _localDB = new DbContext();

            _unsortedNoteListSource = _localDB.UnsortedNoteListSource;
            UnsortedNotes = new UnsortedNotesViewModel(_unsortedNoteListSource);
            
            _documentListSource = new SourceCache<Document, Guid>(d => d.DocumentId);
            DocumentCollection = new DocumentCollectionViewModel(_unsortedNoteListSource, _documentListSource);

            CreateNewNoteCommand = ReactiveCommand.Create(ExecuteCreateNewNote);

            SelectedMainTabIndex = 0;
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> CreateNewNoteCommand { get; }

        void ExecuteCreateNewNote()
        {
            if (SelectedMainTabIndex != 0)
                SelectedMainTabIndex = 0;
            var newNote = new Note();
            _unsortedNoteListSource.AddOrUpdate(newNote);
            UnsortedNotes.SelectNote(newNote);
        }

        #endregion

        #region Public Properties

        private string _titleBarText;

        public string TitleBarText
        {
            get => _titleBarText;
            set => this.RaiseAndSetIfChanged(ref _titleBarText, value);
        }

        private byte _selectedMainTabIndex;

        public byte SelectedMainTabIndex
        {
            get => _selectedMainTabIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedMainTabIndex, value);
        }

        private UnsortedNotesViewModel _unsortedNotes;

        public UnsortedNotesViewModel UnsortedNotes
        {
            get => _unsortedNotes;
            set => this.RaiseAndSetIfChanged(ref _unsortedNotes, value);
        }

        private DocumentCollectionViewModel _documentCollection;

        public DocumentCollectionViewModel DocumentCollection
        {
            get => _documentCollection;
            set => this.RaiseAndSetIfChanged(ref _documentCollection, value);
        }
        
        #endregion
    }
}
