using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Enums;
using NoteEvolution.DAL.Models;
using System.Linq;

namespace NoteEvolution.ViewModels
{
    public class SourceNotesViewModel : NoteListViewModelBase
    {
        private SourceCache<Note, Guid> _relatedNoteListSource;

        private readonly ReadOnlyObservableCollection<NoteViewModel> _relatedNoteListView;

        public SourceNotesViewModel(SourceCache<Note, Guid> relatedNoteListSource)
        {
            _relatedNoteListSource = relatedNoteListSource;

            HideUsedNotes = true;
            SortOrder = NoteSortOrderType.ModifiedDesc;

            var relatedNoteFilterAddDel = relatedNoteListSource
                .Connect()
                .Select(BuildNotesFilter);
            var relatedNoteFilterChange = relatedNoteListSource
                .Connect()
                .WhenAnyPropertyChanged(new[] { nameof(Note.RelatedTextUnitId), nameof(Note.IsReadonly) })
                .Select(BuildNotesFilter);
            var filterUpdateRequired = this.WhenValueChanged(t => t.HideUsedNotes).Select(_ => Unit.Default);
            var noteComparer = this.WhenValueChanged(t => t.SortOrder).Select(BuildNotesComparer);
            var sortUpdateRequired = _relatedNoteListSource
                .Connect()
                .WhenPropertyChanged(n => n.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _relatedNoteListSource
                .Connect()
                .AutoRefreshOnObservable(_ => relatedNoteFilterChange)
                .Filter(relatedNoteFilterAddDel, filterUpdateRequired)
                .Transform(n => new NoteViewModel(n))
                .Sort(noteComparer, sortUpdateRequired)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _relatedNoteListView)
                .DisposeMany()
                .Subscribe();

            NoteList = new NoteListViewModel(_relatedNoteListView, this);

            // set LastAddedText on new unsorted note added, used to auto focus the textbox
            _relatedNoteListSource
                .Connect()
                .OnItemAdded(t => LastAddedNote = t)
                .DisposeMany()
                .Subscribe();
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
