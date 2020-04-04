using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;

namespace NoteEvolution.ViewModels
{
    public class NoteTreeViewModel : ViewModelBase
    {
        #region Private Properties

        private SourceCache<Note, Guid> _rootNoteListSource;

        private ReadOnlyObservableCollection<NoteViewModel> _rootNoteListView;

        #endregion

        public NoteTreeViewModel(SourceCache<Note, Guid> rootNoteListSource)
        {
            _rootNoteListSource = rootNoteListSource;

            var noteComparer = SortExpressionComparer<NoteViewModel>.Ascending(nvm => nvm.Value.OrderNr);
            var noteWasModified = _rootNoteListSource
                .Connect()
                .WhenPropertyChanged(n => n.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _rootNoteListSource
                .Connect()
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
