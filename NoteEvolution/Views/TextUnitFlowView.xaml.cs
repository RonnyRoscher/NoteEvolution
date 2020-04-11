using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NoteEvolution.Views
{
    public class TextUnitFlowView : UserControl
    {
        public TextUnitFlowView()
        {
            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
