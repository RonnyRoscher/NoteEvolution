using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;
using System.Reflection;

namespace NoteEvolution.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        public MainWindowViewModel()
        {
            CreateNewNoteCommand = ReactiveCommand.Create(ExecuteCreateNewNote);
            DeleteSelectedNoteCommand = ReactiveCommand.Create(ExecuteDeleteSelectedNote);

            _noteListSource = new SourceCache<Note, Guid>(note => note.Id);

            var noteComparer = SortExpressionComparer<Note>.Descending(note => note.ModificationDate);
            var wasModified = _noteListSource.Connect()
                .WhenPropertyChanged(note => note.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _noteListSource
                .Connect()
                .Sort(noteComparer, wasModified)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _noteList)
                .DisposeMany()
                .Subscribe();

            _noteListSource.AddOrUpdate(new List<Note>{
                new Note { Content = "test 1" },
                new Note { Content = "test 2" }
            });
            SelectedNote = _noteListSource.Items.LastOrDefault();
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> CreateNewNoteCommand { get; }

        void ExecuteCreateNewNote()
        {
            var newNote = new Note();
            _noteListSource.AddOrUpdate(newNote);
            SelectedNote = newNote;
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedNoteCommand { get; }

        void ExecuteDeleteSelectedNote()
        {
            if (SelectedNote != null)
            {
                var closestItem = _noteListSource.Items.FirstOrDefault(note => note.ModificationDate > SelectedNote.ModificationDate);
                if (closestItem == null)
                    closestItem = _noteListSource.Items.LastOrDefault(note => note.ModificationDate < SelectedNote.ModificationDate);
                _noteListSource.Remove(SelectedNote);
                SelectedNote = closestItem;
            }
        }

        #endregion

        #region Public Properties

        public string TitleBarText => "NoteEvolution v" + Assembly.GetEntryAssembly().GetName().Version;

        private SourceCache<Note, Guid> _noteListSource;

        private ReadOnlyObservableCollection<Note> _noteList;

        public ReadOnlyObservableCollection<Note> NoteList => _noteList;

        private Note _selectedNote;

        public Note SelectedNote
        {
            get => _selectedNote;
            set => this.RaiseAndSetIfChanged(ref _selectedNote, value);
        }

        #endregion
    }
}
