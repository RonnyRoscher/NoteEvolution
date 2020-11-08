using NoteEvolution.ViewModels;

namespace NoteEvolution.Events
{
    public class NotifyNewDragNotePickedUp
    {
        public NotifyNewDragNotePickedUp(NoteViewModel dragItem)
        {
            DragItem = dragItem;
        }

        public NoteViewModel DragItem { get; set; }
    }
}
