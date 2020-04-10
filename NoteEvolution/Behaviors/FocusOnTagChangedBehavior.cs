using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;

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
			if (e.Property.Name == nameof(Control.Tag))
			{
				Dispatcher.UIThread.Post(() => AssociatedObject?.Focus(), DispatcherPriority.Layout);
			}
		}
	}
}
