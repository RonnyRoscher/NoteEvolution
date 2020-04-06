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
            /*var focusManager = new FocusManager();
            var tbxNoteText = this.FindControl<TextBox>("tbxNoteText");
            if (tbxNoteText != null)
            {
                //tbxNoteText.Focus();
                //focusManager.Focus(tbxNoteText);
                focusManager.SetFocusedElement(this, tbxNoteText);
            }*/
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
