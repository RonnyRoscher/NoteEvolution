using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using NoteEvolution.Events;
using NoteEvolution.ViewModels;
using PubSub;
using System.Linq;

namespace NoteEvolution.Views
{
    public class ContentPropertiesView : UserControl
    {
        private readonly Hub _eventAggregator;
        private ListBox _lbNoteList;

        public ContentPropertiesView()
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

        public void EndMoveOperation(object sender, PointerReleasedEventArgs e)
        {
            var hoveredItem = (ListBoxItem)_lbNoteList.GetLogicalChildren().FirstOrDefault(x => this.GetVisualsAt(e.GetPosition(this)).Contains(((IVisual)x).GetVisualChildren().First()));
            if (hoveredItem != null)
                _eventAggregator.Publish(new NotifyNewDragNotePickupCanceled());
        }
    }
}
