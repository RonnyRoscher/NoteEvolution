using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;

namespace NoteEvolution.ViewModels
{
    public class NoteListViewModel : ViewModelBase
    {
        #region Private Properties

        private readonly ReadOnlyObservableCollection<NoteViewModel> _noteListView;

        #endregion

        public NoteListViewModel(ReadOnlyObservableCollection<NoteViewModel> unsortedNoteListView, NoteListViewModelBase parent)
        {
            _noteListView = unsortedNoteListView;
            _parent = parent;

            ChangedSelection = this
                .WhenPropertyChanged(nlvm => nlvm.SelectedItem)
                .Where(nlvm => nlvm.Value != null)
                .Select(nlvm => nlvm.Value);
        }

        #region Public Methods

        public void SelectNote(Note note)
        {
            if (note != null && SelectedItem?.Value?.Id != note.Id)
            {
                var newSelection = _noteListView.FirstOrDefault(t => t.Value.Id == note.Id);
                if (newSelection != null)
                    SelectedItem = newSelection;
            }
        }

        #endregion

        #region Public Properties

        public ReadOnlyObservableCollection<NoteViewModel> Items => _noteListView;

        private NoteViewModel _selectedItem;

        public NoteViewModel SelectedItem
        {
            get => _selectedItem;
            set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
        }

        private NoteListViewModelBase _parent;

        public NoteListViewModelBase Parent
        {
            get => _parent;
            set => this.RaiseAndSetIfChanged(ref _parent, value);
        }

        #endregion

        #region Public Observables

        public IObservable<NoteViewModel> ChangedSelection { get; private set; }

        #endregion
    }
}
