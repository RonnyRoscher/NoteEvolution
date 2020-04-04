using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using NoteEvolution.ViewModels;
using System;

namespace NoteEvolution.UserControls
{
    public class NoteTreeControl : UserControl
    {
        public NoteTreeControl()
        {
            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
