using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NoteEvolution.Views
{
    public class DocumentListView : UserControl
    {
        public DocumentListView()
        {
            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
