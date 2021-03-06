using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;
using PubSub;
using NoteEvolution.Events;

namespace NoteEvolution.ViewModels
{
    public class UnsortedNotesViewModel : ViewModelBase
    {
        private readonly Hub _eventAggregator;

        private readonly SourceCache<ContentSource, Guid> _contentSourceListSource;
        private readonly SourceCache<Note, Guid> _noteListSource;

        private readonly ReadOnlyObservableCollection<NoteViewModel> _noteListView;

        public UnsortedNotesViewModel(SourceCache<Note, Guid> noteListSource, SourceCache<ContentSource, Guid> contentSourceListSource)
        {
            _eventAggregator = Hub.Default;

            _contentSourceListSource = contentSourceListSource;
            _noteListSource = noteListSource;

            NoteProperties = new NotePropertiesViewModel(_contentSourceListSource);

            var unsortedNoteFilterAddDel = noteListSource
                .Connect()
                .Select(BuildUnsortedNotesFilter);
            var unsortedNoteFilterChange = noteListSource
                .Connect()
                .WhenAnyPropertyChanged(new[] { nameof(Note.RelatedTextUnitId) })
                .Select(BuildUnsortedNotesFilter);
            var noteComparer = SortExpressionComparer<NoteViewModel>.Descending(nvm => nvm.Value.ModificationDate);
            var noteWasModified = _noteListSource
                .Connect()
                .WhenPropertyChanged(n => n.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _noteListSource
                .Connect()
                .AutoRefreshOnObservable(_ => unsortedNoteFilterChange)
                .Filter(unsortedNoteFilterAddDel)
                .Transform(n => new NoteViewModel(n))
                .Sort(noteComparer, noteWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _noteListView)
                .DisposeMany()
                .Subscribe();

            NoteList = new NoteListViewModel(_noteListView);
            NoteList.ChangedSelection.Do(nvm => { _eventAggregator.Publish(new NotifySelectedUnsortedNoteChanged(nvm)); }).Subscribe();
            SelectNote(_noteListSource.Items.OrderByDescending(n => n.ModificationDate).FirstOrDefault());

            CreateNewNoteCommand = ReactiveCommand.Create(ExecuteCreateNewNote);
            DeleteSelectedNoteCommand = ReactiveCommand.Create(ExecuteDeleteSelectedNote);
        }

        private Func<Note, bool> BuildUnsortedNotesFilter(object param)
        {
            return n => n.RelatedTextUnitId == null;
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> CreateNewNoteCommand { get; }

        void ExecuteCreateNewNote()
        {
            var newNote = new Note();
            _noteListSource.AddOrUpdate(newNote);
            SelectNote(newNote);
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedNoteCommand { get; }

        void ExecuteDeleteSelectedNote()
        {
            if (NoteList.SelectedItem != null)
            {
                var delIdx = NoteList.Items.IndexOf(NoteList.SelectedItem);
                var closestItem = (NoteList.Items.Count > delIdx + 1) ? NoteList.Items.ElementAt(delIdx + 1) : (delIdx > 0 ? NoteList.Items.ElementAt(delIdx - 1) : null);
                _noteListSource.Remove(NoteList.SelectedItem.Value);
                SelectNote(closestItem?.Value);
            }
        }

        #endregion

        #region Public Methods

        public void SelectNote(Note note)
        {
            NoteList.SelectNote(note);
        }

        #endregion

        #region Public Properties

        private NoteListViewModel _noteList;

        public NoteListViewModel NoteList
        {
            get => _noteList;
            set => this.RaiseAndSetIfChanged(ref _noteList, value);
        }

        private NotePropertiesViewModel _noteProperties;

        public NotePropertiesViewModel NoteProperties
        {
            get => _noteProperties;
            set => this.RaiseAndSetIfChanged(ref _noteProperties, value);
        }

        #endregion
    }
}
