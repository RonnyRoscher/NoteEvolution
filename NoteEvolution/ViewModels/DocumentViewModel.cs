using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;
using System.Linq;

namespace NoteEvolution.ViewModels
{
    public class DocumentViewModel : ViewModelBase
    {
        #region Private Properties

        private SourceCache<Note, Guid> _unsortedNoteListSource;

        private SourceCache<TextUnit, Guid> _documentTextUnitListSource;

        private IObservableCache<TextUnitViewModel, Guid> _textUnitListSource;

        private ReadOnlyObservableCollection<TextUnitViewModel> _textUnitListView;

        private IObservableCache<TextUnitViewModel, Guid> _textUnitRootListSource;

        private ReadOnlyObservableCollection<TextUnitViewModel> _textUnitRootListView;

        #endregion

        public DocumentViewModel(SourceCache<Note, Guid> unsortedNoteListSource, Document documentSource)
        {
            _unsortedNoteListSource = unsortedNoteListSource;
            DocumentSource = documentSource;
            _documentTextUnitListSource = documentSource.TextUnitListSource;

            _textUnitListSource = _documentTextUnitListSource
                .Connect()
                .Transform(tu => new TextUnitViewModel(tu, this))
                .DisposeMany()
                .AsObservableCache();
            var textUnitComparer = SortExpressionComparer<TextUnitViewModel>.Ascending(tuvm => tuvm.Value.OrderNr);
            var textUnitWasModified = _textUnitListSource
                .Connect()
                .WhenPropertyChanged(tu => tu.Value.OrderNr)
                .Select(_ => Unit.Default);
            _textUnitListSource
                .Connect()
                .Sort(textUnitComparer, textUnitWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _textUnitListView)
                .DisposeMany()
                .Subscribe();

            _textUnitRootListSource = _textUnitListSource
                .Connect()
                .Filter(tu => tu.Value.Parent == null)
                .DisposeMany()
                .AsObservableCache();
            var rootTextUnitWasModified = _textUnitRootListSource
                .Connect()
                .WhenPropertyChanged(tu => tu.Value.OrderNr)
                .Select(_ => Unit.Default);
            _textUnitRootListSource
                .Connect()
                .Sort(textUnitComparer, rootTextUnitWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _textUnitRootListView)
                .DisposeMany()
                .Subscribe();

            ChangedSelection = this
                .WhenPropertyChanged(ntvm => ntvm.SelectedItem)
                .Where(ntvm => ntvm.Value?.Value != null)
                .Select(ntvm => ntvm.Value.Value);

            var rootTextUnit = new TextUnit(documentSource);
            _documentTextUnitListSource.AddOrUpdate(rootTextUnit);

            CreateNewSuccessorCommand = ReactiveCommand.Create(ExecuteCreateNewSuccessor);
            CreateNewChildCommand = ReactiveCommand.Create(ExecuteCreateNewChild);
            RemoveSelectedCommand = ReactiveCommand.Create(ExecuteRemoveSelected);
            DeleteSelectedCommand = ReactiveCommand.Create(ExecuteDeleteSelected);
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> CreateNewSuccessorCommand { get; }

        void ExecuteCreateNewSuccessor()
        {
            if (SelectedItem != null)
            {
                var newTextUnit = SelectedItem.Value.AddSuccessor();
                if (newTextUnit != null)
                    SelectTextUnit(newTextUnit);
            }
        }

        public ReactiveCommand<Unit, Unit> CreateNewChildCommand { get; }

        void ExecuteCreateNewChild()
        {
            if (SelectedItem != null)
            {
                var newTextUnit = SelectedItem.Value.AddChild();
                if (newTextUnit != null)
                    SelectTextUnit(newTextUnit);
            }
        }

        public ReactiveCommand<Unit, Unit> RemoveSelectedCommand { get; }

        void ExecuteRemoveSelected()
        {
            // move related texts to unsorted notes before removing the note from the document
            foreach (var note in SelectedItem.Value.NoteList)
            {
                _unsortedNoteListSource.AddOrUpdate(note);
            }
            ExecuteDeleteSelected();
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }

        void ExecuteDeleteSelected()
        {
            if (SelectedItem != null)
            {
                var closestItem = _textUnitListView.FirstOrDefault(note => note.Value.OrderNr > SelectedItem.Value.OrderNr);
                if (closestItem == null)
                    closestItem = _textUnitListView.LastOrDefault(note => note.Value.OrderNr < SelectedItem.Value.OrderNr);
                SelectedItem.Value.RelatedDocument.RemoveTextUnit(SelectedItem.Value);
                SelectedItem = (SelectedItem != closestItem) ? closestItem : null;
            }
        }

        #endregion

        #region Public Methods

        public void SelectTextUnit(TextUnit newTextUnitSelection)
        {
            if (newTextUnitSelection != null && SelectedItem?.Value?.TextUnitId != newTextUnitSelection.TextUnitId)
            {
                var newSelection = _textUnitListView.FirstOrDefault(nvm => nvm.Value.TextUnitId == newTextUnitSelection.TextUnitId);
                if (newSelection != null)
                    SelectedItem = newSelection;
            }
        }

        #endregion

        #region Public Properties

        public Document DocumentSource { get; }

        public IObservableCache<TextUnitViewModel, Guid> TextUnitListSource => _textUnitListSource;

        public ReadOnlyObservableCollection<TextUnitViewModel> AllItems => _textUnitListView;

        public ReadOnlyObservableCollection<TextUnitViewModel> RootItems => _textUnitRootListView;

        private TextUnitViewModel _selectedItem;

        public TextUnitViewModel SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (value?.Value?.TextUnitId != null && value.Value.TextUnitId != _selectedItem?.Value?.TextUnitId)
                    this.RaiseAndSetIfChanged(ref _selectedItem, value);
            }
        }

        #endregion

        #region Public Observables

        public IObservable<TextUnit> ChangedSelection { get; private set; }

        #endregion
    }
}
