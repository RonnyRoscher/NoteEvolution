using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using NoteEvolution.Events;
using NoteEvolution.ViewModels;
using PubSub;
using System.Linq;

namespace NoteEvolution.Views
{
    public class NoteListView : UserControl
    {
        private readonly Hub _eventAggregator;
        private ListBox _lbNoteList;

        public NoteListView()
        {
            _eventAggregator = Hub.Default;

            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            _lbNoteList = this.Find<ListBox>("LvNoteList");
        }

        public void StartMoveOperation(object sender, PointerPressedEventArgs e)
        {
            var dragItem = _lbNoteList.GetLogicalChildren().Cast<ListBoxItem>().Single(x => x.IsPointerOver);
            if (dragItem?.DataContext is NoteViewModel dragNote)
                _eventAggregator.Publish(new NotifyNewDragNotePickedUp(dragNote));
        }
    }
}
