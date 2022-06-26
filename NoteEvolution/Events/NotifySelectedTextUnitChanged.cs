using NoteEvolution.DAL.Models;

namespace NoteEvolution.Events
{
    public class NotifySelectedTextUnitChanged
    {
        public NotifySelectedTextUnitChanged(TextUnit newSelectedTextUnit)
        {
            SelectedTextUnit = newSelectedTextUnit;
        }

        public TextUnit SelectedTextUnit { get; set; }
    }
}
