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

        private SourceCache<Note, Guid> _noteListSource;

        private ReadOnlyObservableCollection<NoteViewModel> _noteListView;

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
                .Transform(x => new NoteViewModel(x))
                .Sort(noteComparer, noteWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _noteListView)
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

        public ReadOnlyObservableCollection<NoteViewModel> Items => _noteListView;

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
