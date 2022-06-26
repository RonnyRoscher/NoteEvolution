namespace NoteEvolution.DAL.Events
{
    public class NotifySaveStateChanged
    {
        public NotifySaveStateChanged(bool hasUnsavedChanged)
        {
            HasUnsavedChanged = hasUnsavedChanged;
        }

        public bool HasUnsavedChanged { get; set; }
    }
}
