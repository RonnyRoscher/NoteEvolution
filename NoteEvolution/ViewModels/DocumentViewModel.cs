using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;

namespace NoteEvolution.ViewModels
{
    public class DocumentViewModel : ViewModelBase
    {
        #region Private Properties

        private readonly IObservableCache<TextUnitViewModel, int> _textUnitListSource;

        private readonly ReadOnlyObservableCollection<TextUnitViewModel> _textUnitListView;

        private readonly IObservableCache<TextUnitViewModel, int> _textUnitRootListSource;

        private readonly ReadOnlyObservableCollection<TextUnitViewModel> _textUnitRootListView;

        #endregion

        public DocumentViewModel(Document documentSource)
        {
            DocumentSource = documentSource;

            _textUnitListSource = DocumentSource.TextUnitListSource
                .Transform(t => new TextUnitViewModel(t, this))
                .DisposeMany()
                .AsObservableCache();
            var textUnitComparer = SortExpressionComparer<TextUnitViewModel>.Ascending(tvm => tvm.Value.OrderNr);
            var textUnitWasModified = _textUnitListSource
                .Connect()
                .WhenPropertyChanged(t => t.Value.OrderNr)
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
                .Filter(t => t.Value.Parent == null)
                .DisposeMany()
                .AsObservableCache();
            var rootTextUnitWasModified = _textUnitRootListSource
                .Connect()
                .WhenPropertyChanged(t => t.Value.OrderNr)
                .Select(_ => Unit.Default);
            _textUnitRootListSource
                .Connect()
                .Sort(textUnitComparer, rootTextUnitWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _textUnitRootListView)
                .DisposeMany()
                .Subscribe();

            // keep max tree depth updated in root text unit
            _textUnitRootListSource
                .Connect()
                .WhenPropertyChanged(t => t.Value.SubtreeDepth)
                .Select(cv => RootItems.Max(t => t.Value.SubtreeDepth))
                .Where(nv => nv != TreeMaxDepth)
                .Do(nv => TreeMaxDepth = nv)
                .Subscribe();
            this.WhenAnyValue(t => t.TreeMaxDepth)
                .Select(cv => 12.0 + (TreeMaxDepth * 4.0))
                .Do(nv => MaxFontSize = nv)
                .Subscribe();

            ChangedSelection = this
                .WhenPropertyChanged(ntvm => ntvm.SelectedItem)
                .Where(ntvm => ntvm.Value?.Value != null)
                .Select(ntvm => ntvm.Value.Value);

            var rootTextUnit = new TextUnit(documentSource);
            DocumentSource.GlobalTextUnitListSource.AddOrUpdate(rootTextUnit);

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
                DocumentSource.GlobalNoteListSource.AddOrUpdate(note);
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
            if (newTextUnitSelection != null && SelectedItem?.Value?.Id != newTextUnitSelection.Id)
            {
                var newSelection = _textUnitListView.FirstOrDefault(nvm => nvm.Value.Id == newTextUnitSelection.Id);
                if (newSelection != null)
                    SelectedItem = newSelection;
            }
        }

        public void CreateNewSuccessor(NoteViewModel sourceNote)
        {
            if (SelectedItem != null)
            {
                var newTextUnit = SelectedItem.Value.AddSuccessor();
                if (newTextUnit != null)
                {
                    SelectTextUnit(newTextUnit);
                    newTextUnit.NoteList.FirstOrDefault().Text = sourceNote.Value.Text;
                    if (!sourceNote.Value.Usage.ContainsKey(DocumentSource.Id))
                        sourceNote.Value.Usage.Add(DocumentSource.Id, new System.Collections.Generic.HashSet<int>());
                    if (!sourceNote.Value.Usage[DocumentSource.Id].Contains(newTextUnit.Id))
                        sourceNote.Value.Usage[DocumentSource.Id].Add(newTextUnit.Id);
                    sourceNote.Value.IsReadonly = true;
                }
            }
        }

        #endregion

        #region Public Properties

        public Document DocumentSource { get; }

        public IObservableCache<TextUnitViewModel, int> TextUnitListSource => _textUnitListSource;

        public ReadOnlyObservableCollection<TextUnitViewModel> AllItems => _textUnitListView;

        public ReadOnlyObservableCollection<TextUnitViewModel> RootItems => _textUnitRootListView;

        private int _treeMaxDepth;

        public int TreeMaxDepth
        {
            get => _treeMaxDepth;
            set => this.RaiseAndSetIfChanged(ref _treeMaxDepth, value);
        }

        private double _maxFontSize;

        public double MaxFontSize
        {
            get => _maxFontSize;
            set => this.RaiseAndSetIfChanged(ref _maxFontSize, value);
        }

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
