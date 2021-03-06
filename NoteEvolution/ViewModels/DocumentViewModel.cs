using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;
using System.Collections.Generic;
using PubSub;
using NoteEvolution.Events;

namespace NoteEvolution.ViewModels
{
    public class DocumentViewModel : ViewModelBase
    {
        #region Private Properties
        private readonly Hub _eventAggregator;

        private IObservableCache<TextUnitViewModel, Guid> _textUnitListSource;

        private readonly ReadOnlyObservableCollection<TextUnitViewModel> _textUnitListView;

        private readonly IObservableCache<TextUnitViewModel, Guid> _textUnitRootListSource;

        private readonly ReadOnlyObservableCollection<TextUnitViewModel> _textUnitRootListView;

        #endregion

        public DocumentViewModel(Document documentSource)
        {
            _eventAggregator = Hub.Default;

            Value = documentSource;

            TextUnitListSource = Value.TextUnitListSource
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
            ChangedSelection.Do(tvm => { _eventAggregator.Publish(new NotifySelectedTextUnitChanged(tvm)); }).Subscribe();

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
            if (SelectedItem != null)
            {
                var delNotes = new List<Note>();
                foreach (var note in SelectedItem.Value.NoteList)
                {
                    var sourceNote = Value.GlobalNoteListSource.Items.FirstOrDefault(n => n.DerivedNotes.Contains(note));
                    // if source note exists, unlock it and delete the derived one
                    if (sourceNote != null)
                    {
                        note.SourceNotes.Remove(sourceNote);
                        sourceNote.DerivedNotes.Remove(note);
                        if (sourceNote.SourceNotes.Count == 0)
                            delNotes.Add(note);
                    }
                    // move related texts to unsorted notes before removing the note from the document (by removing their document association)
                    note.RelatedTextUnitId = null;
                    note.RelatedTextUnit = null;
                }
                if (delNotes.Count > 0)
                    Value.GlobalNoteListSource.Remove(delNotes);
                SelectedItem.Value.NoteList.Clear();
                var closestItem = _textUnitListView.FirstOrDefault(note => note.Value.OrderNr > SelectedItem.Value.OrderNr);
                if (closestItem == null)
                    closestItem = _textUnitListView.LastOrDefault(note => note.Value.OrderNr < SelectedItem.Value.OrderNr);
                SelectedItem.Value.RelatedDocument.RemoveTextUnit(SelectedItem.Value);
                SelectedItem = (SelectedItem != closestItem) ? closestItem : null;
            }
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }

        void ExecuteDeleteSelected()
        {
            if (SelectedItem != null)
            {
                var closestItem = _textUnitListView.FirstOrDefault(note => note.Value.OrderNr > SelectedItem.Value.OrderNr);
                if (closestItem == null)
                    closestItem = _textUnitListView.LastOrDefault(note => note.Value.OrderNr < SelectedItem.Value.OrderNr);
                SelectedItem.Value.RelatedDocument.DeleteTextUnit(SelectedItem.Value);
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
                    var newInitialNote = newTextUnit.NoteList.FirstOrDefault();
                    if (newInitialNote != null)
                    {
                        newInitialNote.Text = sourceNote.Value.Text;
                        newInitialNote.SourceNotes.Add(sourceNote.Value);
                        sourceNote.Value.DerivedNotes.Add(newInitialNote);
                        if (sourceNote.Value.RelatedSources.Count > 0)
                        {
                            foreach (var relatedSource in sourceNote.Value.RelatedSources)
                            {
                                var newTextUnitSource = relatedSource.Copy(false);
                                newTextUnitSource.RelatedTextUnit = newTextUnit;
                                Value.GlobalContentSourceListSource.AddOrUpdate(newTextUnitSource);
                            }
                        }
                    }
                    SelectTextUnit(newTextUnit);
                }
            }
        }

        #endregion

        #region Public Properties

        public Document Value { get; }

        public IObservableCache<TextUnitViewModel, Guid> TextUnitListSource
        {
            get => _textUnitListSource;
            set => this.RaiseAndSetIfChanged(ref _textUnitListSource, value);
        }

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
