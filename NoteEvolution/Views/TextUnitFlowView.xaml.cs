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
    public class TextUnitFlowView : UserControl
    {
        private readonly Hub _eventAggregator;
        private ListBox _lbFlowDocument;
        private ListBoxItem _dragItem;

        public TextUnitFlowView()
        {
            _eventAggregator = Hub.Default;
            _eventAggregator.Subscribe<NotifyNewDragNotePickedUp>(this, newDragItem => { _dragItem = new ListBoxItem { DataContext = newDragItem.DragItem }; });

            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            _lbFlowDocument = this.Find<ListBox>("lbFlowDocument");
        }

        private void ClearDropStyling()
        {
            foreach (ListBoxItem item in _lbFlowDocument.GetLogicalChildren())
                item.Classes.RemoveAll(new[] { "BlackTop", "BlackBottom" });
        }

        public void StartMoveOperation(object sender, PointerPressedEventArgs e) =>
            _dragItem = _lbFlowDocument.GetLogicalChildren().Cast<ListBoxItem>().Single(x => x.IsPointerOver);

        public void MoveOperation(object sender, PointerEventArgs e)
        {
            if (_dragItem == null) return;

            var hoveredItem = (ListBoxItem)_lbFlowDocument.GetLogicalChildren().FirstOrDefault(x => this.GetVisualsAt(e.GetPosition(this)).Contains(((IVisual)x).GetVisualChildren().First()));
            var dragItemIndex = _lbFlowDocument.GetLogicalChildren().ToList().IndexOf(_dragItem);
            var hoveredItemIndex = _lbFlowDocument.GetLogicalChildren().ToList().IndexOf(hoveredItem);

            ClearDropStyling();
            if (hoveredItem != _dragItem) hoveredItem?.Classes.Add(dragItemIndex > hoveredItemIndex ? "BlackTop" : "BlackBottom");
        }

        public void EndMoveOperation(object sender, PointerReleasedEventArgs e)
        {
            var hoveredItem = (ListBoxItem)_lbFlowDocument.GetLogicalChildren().FirstOrDefault(x => this.GetVisualsAt(e.GetPosition(this)).Contains(((IVisual)x).GetVisualChildren().First()));
            if (_dragItem != null && hoveredItem != null && _dragItem != hoveredItem)
            {
                if (_lbFlowDocument?.DataContext is DocumentViewModel vm && _dragItem?.DataContext is NoteViewModel dragNote)
                {
                    vm.CreateNewSuccessor(dragNote);
                }
                _dragItem = null;
            }
            ClearDropStyling();
            _dragItem = null;
        }
    }
}
