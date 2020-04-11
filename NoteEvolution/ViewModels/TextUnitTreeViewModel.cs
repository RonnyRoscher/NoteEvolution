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
    public class TextUnitTreeViewModel : ViewModelBase
    {
        #region Private Properties

        private SourceCache<Note, Guid> _unsortedNoteListSource;

        private SourceCache<TextUnit, Guid> _documentNoteListSource;

        private ReadOnlyObservableCollection<TextUnitViewModel> _rootNoteListView;

        private ReadOnlyObservableCollection<TextUnitViewModel> _noteListView;

        #endregion

        public TextUnitTreeViewModel(SourceCache<Note, Guid> unsortedNoteListSource, SourceCache<TextUnit, Guid> documentNoteListSource)
        {
            CreateNewSuccessorCommand = ReactiveCommand.Create(ExecuteCreateNewSuccessor);
            CreateNewChildCommand = ReactiveCommand.Create(ExecuteCreateNewChild);
            RemoveSelectedCommand = ReactiveCommand.Create(ExecuteRemoveSelected);
            DeleteSelectedCommand = ReactiveCommand.Create(ExecuteDeleteSelected);

            _unsortedNoteListSource = unsortedNoteListSource;

            _documentNoteListSource = documentNoteListSource;
            _documentNoteListSource
                .Connect()
                .Transform(n => new TextUnitViewModel(n))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _noteListView)
                .DisposeMany()
                .Subscribe();

            var noteComparer = SortExpressionComparer<TextUnitViewModel>.Ascending(tuvm => tuvm.Value.OrderNr);
            var noteWasModified = _documentNoteListSource
                .Connect()
                .WhenPropertyChanged(tu => tu.OrderNr)
                .Select(_ => Unit.Default);
            _documentNoteListSource
                .Connect()
                // without this delay, the treeview sometimes cause the item not to be added as well a a crash on bringing the treeview into view
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Filter(tu => tu.Parent == null)
                .Transform(tu => new TextUnitViewModel(tu))
                .Sort(noteComparer, noteWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _rootNoteListView)
                .DisposeMany()
                .Subscribe();

            ChangedSelection = this
                .WhenPropertyChanged(ntvm => ntvm.SelectedItem)
                .Where(ntvm => ntvm.Value?.Value != null)
                .Select(ntvm => ntvm.Value.Value);
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> CreateNewSuccessorCommand { get; }

        void ExecuteCreateNewSuccessor()
        {
            var newTextUnit = SelectedItem.Value.AddSuccessor();
            if (newTextUnit != null)
                SelectTextUnit(newTextUnit);
        }

        public ReactiveCommand<Unit, Unit> CreateNewChildCommand { get; }

        void ExecuteCreateNewChild()
        {
            var newTextUnit = SelectedItem.Value.AddChild();
            if (newTextUnit != null)
                SelectTextUnit(newTextUnit);
        }

        public ReactiveCommand<Unit, Unit> RemoveSelectedCommand { get; }

        void ExecuteRemoveSelected()
        {
            // move related texts to unsorted notes before removing the note from the document
            foreach (var note in SelectedItem.Value.Content)
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
                var closestItem = _noteListView.FirstOrDefault(note => note.Value.OrderNr > SelectedItem.Value.OrderNr);
                if (closestItem == null)
                    closestItem = _noteListView.LastOrDefault(note => note.Value.OrderNr < SelectedItem.Value.OrderNr);
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
                var newSelection = _noteListView.FirstOrDefault(nvm => nvm.Value.TextUnitId == newTextUnitSelection.TextUnitId);
                if (newSelection != null)
                    SelectedItem = newSelection;
            }
        }

        public SourceCache<TextUnit, Guid> GetDocumentNoteListSource()
        {
            return _documentNoteListSource;
        }

        #endregion

        #region Public Properties

        public ReadOnlyObservableCollection<TextUnitViewModel> Items => _rootNoteListView;

        private TextUnitViewModel _selectedItem;

        public TextUnitViewModel SelectedItem
        {
            get => _selectedItem;
            set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
        }

        #endregion

        #region Public Observables

        public IObservable<TextUnit> ChangedSelection { get; private set; }

        #endregion
    }
}
