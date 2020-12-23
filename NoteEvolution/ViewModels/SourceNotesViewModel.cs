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
        private SourceCache<Note, Guid> _relatedNoteListSource;

        private readonly ReadOnlyObservableCollection<NoteViewModel> _relatedNoteListView;

        public SourceNotesViewModel(SourceCache<Note, Guid> relatedNoteListSource)
        {
            _relatedNoteListSource = relatedNoteListSource;

            var relatedNoteFilter = relatedNoteListSource
                .Connect()
                .WhenAnyPropertyChanged(new[] { nameof(Note.RelatedTextUnitId) })
                .Select(BuildSourceNotesFilter);
            var noteComparer = SortExpressionComparer<NoteViewModel>.Descending(nvm => nvm.Value.ModificationDate);
            var noteWasModified = _relatedNoteListSource
                .Connect()
                .WhenPropertyChanged(n => n.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _relatedNoteListSource
                .Connect()
                .Filter(relatedNoteFilter)
                .Transform(n => new NoteViewModel(n))
                .Sort(noteComparer, noteWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _relatedNoteListView)
                .DisposeMany()
                .Subscribe();

            NoteList = new NoteListViewModel(_relatedNoteListView);

            // set LastAddedText on new unsorted note added, used to auto focus the textbox
            _relatedNoteListSource
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
