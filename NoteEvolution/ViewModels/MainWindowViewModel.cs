﻿using System;
using System.Reflection;
using DynamicData;
using ReactiveUI;
using NoteEvolution.Models;
using System.Reactive;

namespace NoteEvolution.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly SourceCache<Note, Guid> _unsortedNoteListSource;
        private readonly SourceCache<Document, Guid> _documentListSource;

        public MainWindowViewModel()
        {
            CreateNewNoteCommand = ReactiveCommand.Create(ExecuteCreateNewNote);

            SelectedMainTabIndex = 0;

            _unsortedNoteListSource = new SourceCache<Note, Guid>(n => n.NoteId);
            UnsortedNotesView = new UnsortedNotesViewModel(_unsortedNoteListSource);
            
            _documentListSource = new SourceCache<Document, Guid>(d => d.DocumentId);
            DocumentCollectionView = new DocumentCollectionViewModel(_unsortedNoteListSource, _documentListSource);
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> CreateNewNoteCommand { get; }

        void ExecuteCreateNewNote()
        {
            if (SelectedMainTabIndex != 0)
                SelectedMainTabIndex = 0;
            var newNote = new Note();
            _unsortedNoteListSource.AddOrUpdate(newNote);
            UnsortedNotesView.SelectNote(newNote);
        }

        #endregion

        #region Public Properties

        public string TitleBarText => "NoteEvolution v" + Assembly.GetEntryAssembly().GetName().Version;

        private byte _selectedMainTabIndex;

        public byte SelectedMainTabIndex
        {
            get => _selectedMainTabIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedMainTabIndex, value);
        }

        private UnsortedNotesViewModel _unsortedNotesView;

        public UnsortedNotesViewModel UnsortedNotesView
        {
            get => _unsortedNotesView;
            set => this.RaiseAndSetIfChanged(ref _unsortedNotesView, value);
        }

        private DocumentCollectionViewModel _documentCollectionView;

        public DocumentCollectionViewModel DocumentCollectionView
        {
            get => _documentCollectionView;
            set => this.RaiseAndSetIfChanged(ref _documentCollectionView, value);
        }
        
        #endregion
    }
}
