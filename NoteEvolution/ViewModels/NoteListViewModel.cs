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
    public class NoteListViewModel : ViewModelBase
    {
        #region Private Properties

        private readonly SourceCache<Note, Guid> _noteListSource;

        private readonly ReadOnlyObservableCollection<NoteViewModel> _noteListView;

        #endregion

        public NoteListViewModel(SourceCache<Note, Guid> noteListSource)
        {
            _noteListSource = noteListSource;

            var noteComparer = SortExpressionComparer<NoteViewModel>.Descending(nvm => nvm.Value.ModificationDate);
            var noteWasModified = _noteListSource
                .Connect()
                .WhenPropertyChanged(n => n.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _noteListSource
                .Connect()
                .Transform(n => new NoteViewModel(n))
                .Sort(noteComparer, noteWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _noteListView)
                .DisposeMany()
                .Subscribe();

            ChangedSelection = this
                .WhenPropertyChanged(nlvm => nlvm.SelectedItem)
                .Where(nlvm => nlvm.Value != null)
                .Select(nlvm => nlvm.Value);
        }

        #region Public Methods

        public void SelectNote(Note note)
        {
            if (note != null && SelectedItem?.Value?.NoteId != note.NoteId)
            {
                var newSelection = _noteListView.FirstOrDefault(t => t.Value.NoteId == note.NoteId);
                if (newSelection != null)
                    SelectedItem = newSelection;
            }
        }

        #endregion

        #region Public Properties

        public ReadOnlyObservableCollection<NoteViewModel> Items => _noteListView;

        private NoteViewModel _selectedItem;

        public NoteViewModel SelectedItem
        {
            get => _selectedItem;
            set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
        }

        #endregion

        #region Public Observables

        public IObservable<NoteViewModel> ChangedSelection { get; private set; }

        #endregion
    }
}
