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
using System.Xml.Linq;
using System.Collections.Generic;
using System.Globalization;

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
            ImportEvernoteEnexFileCommand = ReactiveCommand.Create(ExecuteImportEvernoteEnexFile);
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

        public ReactiveCommand<Unit, Unit> ImportEvernoteEnexFileCommand { get; }

        void ExecuteImportEvernoteEnexFile()
        {
            Task.Run(() =>
            {
                if (File.Exists("Evernote.enex"))
                {
                    var evernoteFile = XElement.Load("Evernote.enex");

                    string text;
                    string tmp;
                    var provider = CultureInfo.InvariantCulture;

                    foreach (var note in evernoteFile.Elements("note"))
                    {
                        var newNote = new Note();
                        ContentSource contentSource = null;
                        foreach (var noteProperty in note.Elements())
                        {
                            switch (noteProperty.Name.LocalName)
                            {
                                case "title":
                                    if (noteProperty.Value != "Unbenannte Notiz")
                                        newNote.Text = noteProperty.Value + Environment.NewLine + Environment.NewLine;
                                    break;
                                case "created":
                                    if (DateTime.TryParseExact(noteProperty.Value, "yyyyMMddTHHmmssZ", provider, DateTimeStyles.AssumeLocal, out var creationDate))
                                        newNote.CreationDate = newNote.ModificationDate = creationDate;
                                    break;
                                case "note-attributes":
                                    var url = noteProperty.Elements("source-url").FirstOrDefault()?.Value;
                                    if (!string.IsNullOrWhiteSpace(url))
                                        contentSource = new ContentSource { Url = url, CreationDate = newNote.CreationDate, ModificationDate = newNote.ModificationDate };
                                    break;
                                case "content":
                                    tmp = noteProperty.Value.Substring(noteProperty.Value.IndexOf("<?xml "))
                                        .Replace("<div>&nbsp;</div>", "<br/>").Replace("&nbsp;"," ");
                                    var xmlContent = XDocument.Parse(tmp).Elements("en-note").FirstOrDefault();
                                    text = "";
                                    AddFullTagContentR(ref text, xmlContent);
                                    newNote.Text += text;
                                    break;
                            }
                        }
                        if (!_context.NoteListSource.Items.Any(n => n.CreationDate == newNote.CreationDate && n.Text == newNote.Text))
                        {
                            _context.NoteListSource.AddOrUpdate(newNote);
                            if (contentSource != null)
                            {
                                contentSource.RelatedNoteId = newNote.Id;
                                _context.ContentSourceListSource.AddOrUpdate(contentSource);
                                contentSource = null;
                            }
                        }
                    }
                }
            });
        }

        private void AddFullTagContentR(ref string output, XElement tag, int indentationCnt = -1)
        {
            foreach (var node in tag.Nodes())
            {
                if (node is XElement subElement)
                {
                    if (subElement.Descendants().Any())
                    {
                        if (subElement.Name.LocalName == "ul" || subElement.Name.LocalName == "ol")
                        {
                            AddFullTagContentR(ref output, subElement, indentationCnt + 1);
                        } else { 
                            var content = "";
                            AddFullTagContentR(ref content, subElement, indentationCnt);
                            if (subElement.Name.LocalName == "li" && !content.Contains("\u2022"))
                                output += new string('\t', indentationCnt) + "\u2022" + " " + content;
                            else output += content;
                        }
                    } else {
                        if (subElement.Name.LocalName == "br")
                        {
                            output += Environment.NewLine;
                        }
                        else if (subElement.Name.LocalName == "hr")
                        {
                            output += "---" + Environment.NewLine;
                        }
                        else if (subElement.Name.LocalName == "li")
                        {
                            var content = "";
                            AddFullTagContentR(ref content, subElement, indentationCnt);
                            if (subElement.Name.LocalName == "li" && !content.Contains("\u2022"))
                                output += new string('\t', indentationCnt) + "\u2022" + " " + content;
                            else output += content;
                        } else {
                            if (!string.IsNullOrWhiteSpace(output))
                                output += " ";
                            output += subElement.Value.Replace("\n", "").Replace("\r", "") + ((subElement.NextNode is XElement next && next.Name.LocalName == "a") || subElement.Name.LocalName == "a" || subElement.Name.LocalName == "i" || subElement.Name.LocalName == "b" ? "" : Environment.NewLine);
                        }
                    }
                } 
                else if (node is XText textElement)
                {
                    if (!string.IsNullOrWhiteSpace(output))
                        output += " ";
                    output += textElement.Value.Replace("\n", "").Replace("\r", "") + ((textElement.NextNode is XElement next && (next.Name.LocalName == "a" || next.Name.LocalName == "br")) || textElement.Parent?.Name.LocalName == "div" || textElement.Parent?.Name.LocalName == "span" ? "" : Environment.NewLine);
                }
            }
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
