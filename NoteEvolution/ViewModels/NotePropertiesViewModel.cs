using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using PubSub;
using ReactiveUI;
using NoteEvolution.Events;
using NoteEvolution.Models;
using Avalonia.Layout;

namespace NoteEvolution.ViewModels
{
    public class NotePropertiesViewModel : ViewModelBase
    {
        #region Private Properties

        private readonly Hub _eventAggregator;

        private readonly SourceCache<ContentSource, Guid> _contentSourceListSource;

        private ReadOnlyObservableCollection<ContentSource> _currentContentSourceListView;

        #endregion

        public NotePropertiesViewModel(SourceCache<ContentSource, Guid> contentSourceListSource)
        {
            _eventAggregator = Hub.Default;
            _eventAggregator.Subscribe<NotifySelectedUnsortedNoteChanged>(this, newSelection => { CurrentNote = newSelection.SelectedNote; });

            _contentSourceListSource = contentSourceListSource;

            ContentSourceListOrientation = Orientation.Horizontal;

            ChangedSelection = this
                .WhenPropertyChanged(npvm => npvm.CurrentNote)
                .Where(npvm => npvm.Value != null)
                .Select(npvm => npvm.Value);

            var observableFilter = this.WhenAnyValue(vm => vm.CurrentNote).Select(BuildCurrentContentSourceFilter);
            _contentSourceListSource
                .Connect()
                .Filter(observableFilter)
                .Sort(SortExpressionComparer<ContentSource>.Ascending(s => s.CreationDate))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _currentContentSourceListView)
                .DisposeMany()
                .Subscribe();

            AddContentSourceCommand = ReactiveCommand.Create(ExecuteAddContentSource);
            DeleteContentSourceCommand = ReactiveCommand.Create<ContentSource>(ExecuteDeleteContentSource);
        }

        private Func<ContentSource, bool> BuildCurrentContentSourceFilter(object param)
        {
            return n => CurrentNote != null && CurrentNote.Value.Id == n.RelatedNoteId;
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> AddContentSourceCommand { get; }

        void ExecuteAddContentSource()
        {
            if (CurrentNote != null)
            {
                var newContentSource = new ContentSource { RelatedNote = CurrentNote.Value };
                _contentSourceListSource.AddOrUpdate(newContentSource);
                LastAddedContentSource = newContentSource;
            }
        }

        public ReactiveCommand<ContentSource, Unit> DeleteContentSourceCommand { get; }

        void ExecuteDeleteContentSource(ContentSource oldContentSource)
        {
            if (oldContentSource != null)
                _contentSourceListSource.Remove(oldContentSource);
        }

        #endregion

        #region Public Properties

        public ReadOnlyObservableCollection<ContentSource> CurrentContentSourceList => _currentContentSourceListView;

        private NoteViewModel _currentNote;

        public NoteViewModel CurrentNote
        {
            get => _currentNote;
            set => this.RaiseAndSetIfChanged(ref _currentNote, value);
        }

        private ContentSource _lastAddedContentSource;

        public ContentSource LastAddedContentSource
        {
            get => _lastAddedContentSource;
            set => this.RaiseAndSetIfChanged(ref _lastAddedContentSource, value);
        }

        private Orientation _contentSourceListOrientation;

        public Orientation ContentSourceListOrientation
        {
            get => _contentSourceListOrientation;
            set => this.RaiseAndSetIfChanged(ref _contentSourceListOrientation, value);
        }

        #endregion

        #region Public Observables

        public IObservable<NoteViewModel> ChangedSelection { get; private set; }

        #endregion
    }
}
