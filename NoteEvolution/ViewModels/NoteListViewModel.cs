using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;
using System.Linq;

namespace NoteEvolution.ViewModels
{
    public class NoteListViewModel : ViewModelBase
    {
        #region Private Properties

        private readonly ReadOnlyObservableCollection<NoteViewModel> _noteListView;

        #endregion

        public NoteListViewModel(ReadOnlyObservableCollection<NoteViewModel> usnortedNoteListView)
        {
            _noteListView = usnortedNoteListView;

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

        #endregion

        #region Public Observables

        public IObservable<NoteViewModel> ChangedSelection { get; private set; }

        #endregion
    }
}
