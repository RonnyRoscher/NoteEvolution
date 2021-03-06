using NoteEvolution.ViewModels;

namespace NoteEvolution.Events
{
    public class NotifySelectedUnsortedNoteChanged
    {
        public NotifySelectedUnsortedNoteChanged(NoteViewModel newSelectedNote)
        {
            SelectedNote = newSelectedNote;
        }

        public NoteViewModel SelectedNote { get; set; }
    }
}
