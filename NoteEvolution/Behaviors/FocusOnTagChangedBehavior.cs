using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;
using NoteEvolution.Models;
using NoteEvolution.ViewModels;
using System.Linq;

namespace NoteEvolution.Behaviors
{
	public class FocusOnTagChangedBehavior : Behavior<Control>
	{
		protected override void OnAttached()
		{
			base.OnAttached();
			AssociatedObject.PropertyChanged += AssociatedObject_PropertyChanged;
		}

		private void AssociatedObject_PropertyChanged(object sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
		{
			if (e.Property.Name == nameof(Control.Tag) && e.NewValue != null && AssociatedObject.DataContext != null)
			{
				if (e.NewValue is Document || (AssociatedObject.DataContext is Note cn && e.NewValue is TextUnitViewModel ntu && ntu.Value.NoteList.Any(n => n.NoteId == cn.NoteId) && !AssociatedObject.IsFocused))
					Dispatcher.UIThread.Post(() => AssociatedObject?.Focus(), DispatcherPriority.Layout);
			}
		}
	}
}
