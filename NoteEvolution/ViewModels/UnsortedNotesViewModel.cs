using System;
using System.Linq;
using System.Reactive;
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

            CreateNewNoteCommand = ReactiveCommand.Create(ExecuteCreateNewNote);
            DeleteSelectedNoteCommand = ReactiveCommand.Create(ExecuteDeleteSelectedNote);
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> CreateNewNoteCommand { get; }

        void ExecuteCreateNewNote()
        {
            var newNote = new Note();
            _noteListSource.AddOrUpdate(newNote);
            if (newNote != null)
                SelectNote(newNote);
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedNoteCommand { get; }

        void ExecuteDeleteSelectedNote()
        {
            if (NoteListView.SelectedItem != null)
            {
                var closestItem = _noteListSource.Items.FirstOrDefault(note => note.ModificationDate > NoteListView.SelectedItem.Value.ModificationDate);
                if (closestItem == null)
                    closestItem = _noteListSource.Items.LastOrDefault(note => note.ModificationDate < NoteListView.SelectedItem.Value.ModificationDate);
                _noteListSource.Remove(NoteListView.SelectedItem.Value);
                SelectNote(closestItem);
            }
        }

        #endregion

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

        #endregion
    }
}
