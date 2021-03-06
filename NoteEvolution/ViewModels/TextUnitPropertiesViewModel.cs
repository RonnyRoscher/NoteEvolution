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
    public class TextUnitPropertiesViewModel : ViewModelBase
    {
        #region Private Properties

        private readonly Hub _eventAggregator;

        private readonly SourceCache<ContentSource, Guid> _contentSourceListSource;

        private ReadOnlyObservableCollection<ContentSource> _currentContentSourceListView;

        #endregion

        public TextUnitPropertiesViewModel(SourceCache<ContentSource, Guid> contentSourceListSource)
        {
            _eventAggregator = Hub.Default;
            _eventAggregator.Subscribe<NotifySelectedTextUnitChanged>(this, newSelection => { CurrentTextUnit = newSelection.SelectedTextUnit; });

            _contentSourceListSource = contentSourceListSource;

            ContentSourceListOrientation = Orientation.Vertical;

            ChangedSelection = this
                .WhenPropertyChanged(tupvm => tupvm.CurrentTextUnit)
                .Where(tupvm => tupvm.Value != null)
                .Select(tupvm => tupvm.Value);

            var observableFilter = this.WhenAnyValue(vm => vm.CurrentTextUnit).Select(BuildCurrentContentSourceFilter);
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
            return n => CurrentTextUnit != null && CurrentTextUnit.Id == n.RelatedTextUnitId;
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> AddContentSourceCommand { get; }

        void ExecuteAddContentSource()
        {
            if (CurrentTextUnit != null)
            {
                var newContentSource = new ContentSource { RelatedTextUnit = CurrentTextUnit };
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

        private TextUnit _currentTextUnit;

        public TextUnit CurrentTextUnit
        {
            get => _currentTextUnit;
            set => this.RaiseAndSetIfChanged(ref _currentTextUnit, value);
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

        public IObservable<TextUnit> ChangedSelection { get; private set; }

        #endregion
    }
}
