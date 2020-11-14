using System;
using System.Linq;
using DynamicData;
using ReactiveUI;
using NoteEvolution.Models;

namespace NoteEvolution.ViewModels
{
    public class UnsortedNotesViewModel : ViewModelBase
    {
        private SourceCache<Note, Guid> _noteListSource;

        public UnsortedNotesViewModel(SourceCache<Note, Guid> noteListSource)
        {
            _noteListSource = noteListSource;
            NoteListView = new NoteListViewModel(_noteListSource);
            SelectNote(_noteListSource.Items.OrderByDescending(n => n.ModificationDate).FirstOrDefault());

            // set LastAddedText on new unsorted note added, used to auto focus the textbox
            _noteListSource
                .Connect()
                .OnItemAdded(t => LastAddedNote = t)
                .DisposeMany()
                .Subscribe();
        }

        #region Public Methods

        public void SelectNote(Note note)
        {
            NoteListView.SelectNote(note);
        }

        #endregion

        #region Public Properties

        private NoteListViewModel _noteListView;

        public NoteListViewModel NoteListView
        {
            get => _noteListView;
            set => this.RaiseAndSetIfChanged(ref _noteListView, value);
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
