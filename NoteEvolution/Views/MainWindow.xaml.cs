using Avalonia;
using Avalonia.Controls;
//using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace NoteEvolution.Views
{
    public class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
#if DEBUG
            this.AttachDevTools();
#endif
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
