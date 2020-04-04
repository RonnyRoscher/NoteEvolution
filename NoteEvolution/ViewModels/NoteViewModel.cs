using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;

namespace NoteEvolution.ViewModels
{
    public class NoteViewModel : ViewModelBase
    {
        #region Private Properties

        private SourceCache<Note, Guid> _childNoteListSource;

        private ReadOnlyObservableCollection<NoteViewModel> _childNoteListView;

        #endregion

        public NoteViewModel(Note note)
        {
            IsVisible = true;
            IsSelected = false;
            IsExpanded = true;

            Value = note;
            _childNoteListSource = note.GetChildNoteListSource();

            var noteComparer = SortExpressionComparer<NoteViewModel>.Ascending(nvm => nvm.Value.OrderNr);
            var noteWasModified = _childNoteListSource
                .Connect()
                .WhenPropertyChanged(n => n.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _childNoteListSource
                .Connect()
                .Transform(x => new NoteViewModel(x))
                .Sort(noteComparer, noteWasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _childNoteListView)
                .DisposeMany()
                .Subscribe();
        }

        #region Public Properties

        private bool _isVisible;

        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        private bool _isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        private bool _isExpanded;

        public bool IsExpanded
        {
            get => _isExpanded;
            set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
        }

        private Note _value;

        public Note Value
        {
            get => _value;
            set => this.RaiseAndSetIfChanged(ref _value, value);
        }

        public ReadOnlyObservableCollection<NoteViewModel> ChildNoteListView => _childNoteListView;

        #endregion
    }
}
