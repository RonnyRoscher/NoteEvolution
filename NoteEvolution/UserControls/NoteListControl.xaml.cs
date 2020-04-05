using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using NoteEvolution.ViewModels;
using System;

namespace NoteEvolution.UserControls
{
    public class NoteListControl : UserControl
    {
        public NoteListControl()
        {
            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
