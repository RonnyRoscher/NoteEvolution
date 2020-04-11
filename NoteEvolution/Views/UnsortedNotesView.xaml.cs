using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NoteEvolution.Views
{
    public class UnsortedNotesView : UserControl
    {
        public UnsortedNotesView()
        {
            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
