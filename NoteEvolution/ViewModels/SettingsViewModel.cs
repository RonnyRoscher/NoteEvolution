using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using PubSub;
using NoteEvolution.DAL.Models;

namespace NoteEvolution.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        private readonly Hub _eventAggregator;

        private readonly SourceCache<Language, Guid> _languageListSource;

        private readonly ReadOnlyObservableCollection<Language> _languageListView;

        public SettingsViewModel(SourceCache<Language, Guid> languageListSource)
        {
            _eventAggregator = Hub.Default;

            _languageListSource = languageListSource;

            var noteComparer = SortExpressionComparer<Language>.Ascending(l => l.OrderNr);
            var sortUpdateRequired = _languageListSource
                .Connect()
                .WhenPropertyChanged(l => l.OrderNr)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(_ => Unit.Default);
            _languageListSource
                .Connect()
                .Sort(noteComparer, sortUpdateRequired)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _languageListView)
                .DisposeMany()
                .Subscribe();

            AddNewLanguageCommand = ReactiveCommand.Create(ExecuteAddNewLanguage);
            DeleteSelectedLanguageCommand = ReactiveCommand.Create(ExecuteDeleteSelectedLanguage);
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> AddNewLanguageCommand { get; }

        void ExecuteAddNewLanguage()
        {
            var newLanguage = new Language { OrderNr = _languageListSource.Count == 0 ? 0 : _languageListSource.Items.Select(l => l.OrderNr).DefaultIfEmpty(0).Max() + 1 };
            _languageListSource.AddOrUpdate(newLanguage);
            SelectedLanguage = newLanguage;
        }

        public ReactiveCommand<Unit, Unit> DeleteSelectedLanguageCommand { get; }

        void ExecuteDeleteSelectedLanguage()
        {
            if (SelectedLanguage != null)
            {
                var closestItem = (_languageListSource.Count > SelectedLanguage.OrderNr + 1 ) 
                    ? _languageListSource.Items.FirstOrDefault(l => l.OrderNr == SelectedLanguage.OrderNr + 1) 
                    : (SelectedLanguage.OrderNr > 0 ? _languageListSource.Items.FirstOrDefault(l => l.OrderNr == SelectedLanguage.OrderNr - 1) : null);
                // close OrderNr gap
                var successorList = _languageListSource.Items.Where(l => l.OrderNr > SelectedLanguage.OrderNr).ToList();
                foreach (var successor in successorList)
                    successor.OrderNr--;
                _languageListSource.Remove(SelectedLanguage);
                SelectedLanguage = closestItem;
            }
        }

        #endregion

        #region Public Methods


        #endregion

        #region Public Properties

        private Language _selectedLanguage;

        public Language SelectedLanguage
        {
            get => _selectedLanguage;
            set => this.RaiseAndSetIfChanged(ref _selectedLanguage, value);
        }

        //private ObservableCollection<Language> _languages;

        //public ObservableCollection<Language> Languages
        //{
        //    get => _languages;
        //    set => this.RaiseAndSetIfChanged(ref _languages, value);
        //}

        public ReadOnlyObservableCollection<Language> Languages => _languageListView;

        #endregion
    }
}
