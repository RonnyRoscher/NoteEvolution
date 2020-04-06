﻿using System;
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
    public class NoteTreeViewModel : ViewModelBase
    {
        #region Private Properties

        private SourceCache<Note, Guid> _noteListSource;

        private ReadOnlyObservableCollection<NoteViewModel> _rootNoteListView;

        private ReadOnlyObservableCollection<NoteViewModel> _noteListView;

        #endregion

        public NoteTreeViewModel(SourceCache<Note, Guid> noteListSource)
        {
            _noteListSource = noteListSource;
            _noteListSource
                .Connect()
                .Transform(n => new NoteViewModel(n))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _noteListView)
                .DisposeMany()
                .Subscribe();

            var noteComparer = SortExpressionComparer<NoteViewModel>.Ascending(nvm => nvm.Value.OrderNr);
            var noteWasModified = _noteListSource
                .Connect()
                .WhenPropertyChanged(n => n.OrderNr)
                .Select(_ => Unit.Default);
            _noteListSource
                .Connect()
                // without this delay, the treeview sometimes cause the item not to be added as well a a crash on bringing the treeview into view
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Filter(n => n.Parent == null)
                .Transform(x => new NoteViewModel(x))
                .Sort(noteComparer, noteWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _rootNoteListView)
                .DisposeMany()
                .Subscribe();

            ChangedSelection = this
                .WhenPropertyChanged(n => n.SelectedItem)
                .Where(nvm => nvm.Value?.Value != null)
                .Select(nvm => nvm.Value.Value);
        }

        #region Public Methods

        public void SelectNote(Note note)
        {
            if (note != null && SelectedItem?.Value?.NoteId != note.NoteId)
            {
                var newSelection = _noteListView.FirstOrDefault(nvm => nvm.Value.NoteId == note.NoteId);
                if (newSelection != null)
                    SelectedItem = newSelection;
            }
        }

        #endregion

        #region Public Properties

        public ReadOnlyObservableCollection<NoteViewModel> Items => _rootNoteListView;

        private NoteViewModel _selectedItem;

        public NoteViewModel SelectedItem
        {
            get => _selectedItem;
            set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
        }

        #endregion

        #region Public Observables

        public IObservable<Note> ChangedSelection { get; private set; }

        #endregion
    }
}