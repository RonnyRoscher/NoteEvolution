using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NoteEvolution.Views
{
    public class DocumentCollectionView : UserControl
    {
        public DocumentCollectionView()
        {
            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
