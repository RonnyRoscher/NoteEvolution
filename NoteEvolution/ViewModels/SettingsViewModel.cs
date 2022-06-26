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
using System.IO;
using NoteEvolution.DAL.DataContext;
using System.Threading.Tasks;

namespace NoteEvolution.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        private readonly Hub _eventAggregator;

        private readonly SourceCache<Language, Guid> _languageListSource;

        private readonly ReadOnlyObservableCollection<Language> _languageListView;

        private readonly NoteEvolutionContext _context;

        public SettingsViewModel(SourceCache<Language, Guid> languageListSource, NoteEvolutionContext dbContext)
        {
            _eventAggregator = Hub.Default;

            _languageListSource = languageListSource;
            _context = dbContext;

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

            ImportOldNoteEvolutionDbCommand = ReactiveCommand.Create(ExecuteImportOldNoteEvolutionDb);
            AddNewLanguageCommand = ReactiveCommand.Create(ExecuteAddNewLanguage);
            DeleteSelectedLanguageCommand = ReactiveCommand.Create(ExecuteDeleteSelectedLanguage);
        }

        #region Commands

        public ReactiveCommand<Unit, Unit> ImportOldNoteEvolutionDbCommand { get; }

        void ExecuteImportOldNoteEvolutionDb()
        {
            Task.Run(() => { 
                if (File.Exists("NoteEvolution.sql"))
                {
                    using (var sr = new StreamReader("NoteEvolution.sql"))
                    {
                        int pos;
                        string line, newContent;
                        while (!sr.EndOfStream)
                        {
                            line = sr.ReadLine();
                            if (line.StartsWith("INSERT INTO [dbo].[Notes]"))
                            {
                                //VALUES (N'1', N'1', N'Handbuch des Lebens', N'Manual for Life', N'2018-04-14 18:23:51.000', N'2018-04-14 19:11:53.397');
                                pos = line.IndexOf("VALUES (");
                                if (pos != -1)
                                {
                                    newContent = "";
                                    while (!line.StartsWith("GO"))
                                    {
                                        if (newContent != "")
                                            newContent += Environment.NewLine;
                                        newContent += line;
                                        line = sr.ReadLine();
                                    }
                                    newContent = newContent.Substring(pos + 10);
                                    var data = newContent.Split(new string[] { "', N'", ", null, N'", ", null" }, StringSplitOptions.TrimEntries);
                                    if (data.Length > 5)
                                    {
                                        if (DateTime.TryParse(data[4], out var creationDate) && DateTime.TryParse(data[5].Substring(0, 23), out var modificationDate))
                                        {
                                            if (!string.IsNullOrWhiteSpace(data[2]))
                                            {
                                                if (!_context.NoteListSource.Items.Any(n => n.CreationDate == creationDate && n.Text == data[2]))
                                                {
                                                    _context.NoteListSource.AddOrUpdate(new Note
                                                    {
                                                        CreationDate = creationDate,
                                                        ModificationDate = modificationDate,
                                                        Text = data[2]
                                                    });
                                                }
                                            }
                                            if (!string.IsNullOrWhiteSpace(data[3]))
                                            {
                                                if (!_context.NoteListSource.Items.Any(n => n.CreationDate == creationDate && n.Text == data[3]))
                                                {
                                                    _context.NoteListSource.AddOrUpdate(new Note
                                                    {
                                                        CreationDate = creationDate,
                                                        ModificationDate = modificationDate,
                                                        Text = data[3]
                                                    });
                                                }
                                            }
                                        }
                                    }
                                    else if (data.Length > 4)
                                    {

                                        if (DateTime.TryParse(data[3], out var creationDate) && DateTime.TryParse(data[4].Substring(0, 23), out var modificationDate))
                                        {
                                            if (!string.IsNullOrWhiteSpace(data[2]))
                                            {
                                                if (!_context.NoteListSource.Items.Any(n => n.CreationDate == creationDate && n.Text == data[2]))
                                                {
                                                    _context.NoteListSource.AddOrUpdate(new Note
                                                    {
                                                        CreationDate = creationDate,
                                                        ModificationDate = modificationDate,
                                                        Text = data[2]
                                                    });
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }    
                }
            });
        }

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
