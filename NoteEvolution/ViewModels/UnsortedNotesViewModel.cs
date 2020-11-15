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
            NoteList = new NoteListViewModel(_noteListSource);
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
