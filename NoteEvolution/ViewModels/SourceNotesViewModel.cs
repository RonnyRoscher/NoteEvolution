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
    public class SourceNotesViewModel : ViewModelBase
    {
        private SourceCache<Note, int> _noteListSource;

        private readonly ReadOnlyObservableCollection<NoteViewModel> _noteListView;

        public SourceNotesViewModel(SourceCache<Note, int> noteListSource)
        {
            _noteListSource = noteListSource;

            var unsortedNoteFilter = noteListSource
                .Connect()
                .WhenAnyPropertyChanged(new[] { nameof(Note.RelatedTextUnitId) })
                .Select(BuildSourceNotesFilter);
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

            // set LastAddedText on new unsorted note added, used to auto focus the textbox
            _noteListSource
                .Connect()
                .OnItemAdded(t => LastAddedNote = t)
                .DisposeMany()
                .Subscribe();
        }

        private Func<Note, bool> BuildSourceNotesFilter(object param)
        {
            return note =>
            {
                return note.RelatedTextUnitId == null;
            };
        }

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

        private Note _lastAddedNote;

        public Note LastAddedNote
        {
            get => _lastAddedNote;
            set => this.RaiseAndSetIfChanged(ref _lastAddedNote, value);
        }

        #endregion
    }
}
