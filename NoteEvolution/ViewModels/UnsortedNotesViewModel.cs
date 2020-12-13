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
    public class UnsortedNotesViewModel : ViewModelBase
    {
        private SourceCache<Note, int> _noteListSource;

        private readonly ReadOnlyObservableCollection<NoteViewModel> _noteListView;

        public UnsortedNotesViewModel(SourceCache<Note, int> noteListSource)
        {
            _noteListSource = noteListSource;

            var unsortedNoteFilter = noteListSource
                .Connect()
                //.WhenAnyPropertyChanged(new[] { nameof(Note.RelatedTextUnitId) })
                .Select(BuildUnsortedNotesFilter);
            var noteComparer = SortExpressionComparer<NoteViewModel>.Descending(nvm => nvm.Value.ModificationDate);
            var noteWasModified = _noteListSource
                .Connect()
                .WhenPropertyChanged(n => n.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _noteListSource
                .Connect()
                .Filter(unsortedNoteFilter)
                .Transform(n => new NoteViewModel(n))
                .Sort(noteComparer, noteWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _noteListView)
                .DisposeMany()
                .Subscribe();

            NoteList = new NoteListViewModel(_noteListView);
            SelectNote(_noteListSource.Items.OrderByDescending(n => n.ModificationDate).FirstOrDefault());

            CreateNewNoteCommand = ReactiveCommand.Create(ExecuteCreateNewNote);
            DeleteSelectedNoteCommand = ReactiveCommand.Create(ExecuteDeleteSelectedNote);
        }

        private Func<Note, bool> BuildUnsortedNotesFilter(object param)
        {
            return note =>
            {
                return note.RelatedTextUnitId == null;
            };
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

        #endregion
    }
}
