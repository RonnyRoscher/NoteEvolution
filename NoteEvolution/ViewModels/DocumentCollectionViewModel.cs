﻿using System;
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

        public DocumentCollectionViewModel(SourceCache<Note, Guid> globalNoteListSource, SourceCache<TextUnit, Guid> globalTextUnitListSource, SourceCache<Document, Guid> globalDocumentListSource)
        {
            CreateNewDocumentCommand = ReactiveCommand.Create(ExecuteCreateNewDocument);
            DissolveSelectedDocumentCommand = ReactiveCommand.Create(ExecuteDissolveSelectedDocument);
            DeleteSelectedDocumentCommand = ReactiveCommand.Create(ExecuteDeleteSelectedDocument);

            _globalNoteListSource = globalNoteListSource;
            _globalTextUnitListSource = globalTextUnitListSource;
            _globalDocumentListSource = globalDocumentListSource;

            UnsortedNotes = new SourceNotesViewModel(_globalNoteListSource);

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
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> CreateNewDocumentCommand { get; }

        void ExecuteCreateNewDocument()
        {
            var newDocument = new Document(_globalNoteListSource, _globalTextUnitListSource);
            _globalDocumentListSource.AddOrUpdate(newDocument);
            SelectDocument(newDocument);
        }

        public ReactiveCommand<Unit, Unit> DissolveSelectedDocumentCommand { get; }

        void ExecuteDissolveSelectedDocument()
        {
            // move contained notes to unsorted notes
            if (SelectedItem?.Value.TextUnitList.Count() > 0)
            {
                foreach (var textUnit in SelectedItem.Value.TextUnitList)
                {
                    foreach (var note in textUnit.NoteList)
                    {
                        // todo: should move now only mean change relations, as its the same source cache
                        _globalNoteListSource.AddOrUpdate(note);
                    }
                }
            }
            ExecuteDeleteSelectedDocument();
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedDocumentCommand { get; }

        void ExecuteDeleteSelectedDocument()
        {
            var closestItem = _globalDocumentListSource.Items.FirstOrDefault(note => note.ModificationDate > SelectedItem?.Value.ModificationDate);
            if (closestItem == null)
                closestItem = _globalDocumentListSource.Items.LastOrDefault(note => note.ModificationDate < SelectedItem?.Value.ModificationDate);
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

        #endregion

        #region Public Observables

        public IObservable<DocumentViewModel> ChangedSelection { get; private set; }

        #endregion
    }
}
