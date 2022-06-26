using System;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Timers;
using Microsoft.EntityFrameworkCore;
using DynamicData;
using PubSub;
using NoteEvolution.DAL.Events;
using NoteEvolution.DAL.Models;
using Timer = System.Timers.Timer;

namespace NoteEvolution.DAL.DataContext
{
    public class NoteEvolutionContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite("Data Source=local.db");

        #region Virtual Database Tables

        public virtual DbSet<Document>? Documents { get; set; }
        public virtual DbSet<TextUnit>? TextUnits { get; set; }
        public virtual DbSet<Note>? Notes { get; set; }
        public virtual DbSet<ContentSource>? ContentSources { get; set; }
        public virtual DbSet<Language>? Languages { get; set; }

        #endregion

        #region Private Members

        private readonly Hub _eventAggregator;

        private readonly Timer _updateTimer;
        private bool _isSaved;
        private bool _isSaving;

        private int _localLanguageId;
        private readonly ConcurrentDictionary<int, Language> _addedLanguages;
        private readonly ConcurrentDictionary<int, Language> _changedLanguages;
        private readonly ConcurrentDictionary<int, Language> _deletedLanguages;
        private readonly SourceCache<Language, Guid> _languageListSource;

        private int _localDocumentId;
        private readonly ConcurrentDictionary<int, Document> _addedDocuments;
        private readonly ConcurrentDictionary<int, Document> _changedDocuments;
        private readonly ConcurrentDictionary<int, Document> _deletedDocuments;
        private readonly SourceCache<Document, Guid> _documentListSource;

        private int _localTextUnitId;
        private readonly ConcurrentDictionary<int, TextUnit> _addedTextUnits;
        private readonly ConcurrentDictionary<int, TextUnit> _changedTextUnits;
        private readonly ConcurrentDictionary<int, TextUnit> _deletedTextUnits;
        private readonly SourceCache<TextUnit, Guid> _textUnitListSource;

        private int _localNoteId;
        private readonly ConcurrentDictionary<int, Note> _addedNotes;
        private readonly ConcurrentDictionary<int, Note> _changedNotes;
        private readonly ConcurrentDictionary<int, Note> _deletedNotes;
        private readonly SourceCache<Note, Guid> _noteListSource;

        private int _localSourceId;
        private readonly ConcurrentDictionary<int, ContentSource> _addedContentSources;
        private readonly ConcurrentDictionary<int, ContentSource> _changedContentSources;
        private readonly ConcurrentDictionary<int, ContentSource> _deletedContentSources;
        private readonly SourceCache<ContentSource, Guid> _contentSourceListSource;

        #endregion

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TextUnit>()
                .HasMany(parent => parent.TextUnitChildList)
                .WithOne(child => child.Parent)
                .HasForeignKey(child => child.ParentId);

            modelBuilder.Entity<TextUnit>()
                .HasMany(item => item.RelatedSources)
                .WithOne(source => source.RelatedTextUnit)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Note>()
                .HasMany(item => item.RelatedSources)
                .WithOne(source => source.RelatedNote)
                .OnDelete(DeleteBehavior.Cascade);

            base.OnModelCreating(modelBuilder);
        }

        // dotnet ef migrations add MigrationName

        public NoteEvolutionContext()
        {
            Database.Migrate();

            _eventAggregator = Hub.Default;

            _updateTimer = new Timer(3000);
            _updateTimer.Elapsed += OnUpdateTimerElapsedEvent;
            _updateTimer.AutoReset = true;
            _updateTimer.Enabled = true;

            _isSaved = true;
            _isSaving = false;

            _localDocumentId = 1;
            _addedDocuments = new ConcurrentDictionary<int, Document>();
            _changedDocuments = new ConcurrentDictionary<int, Document>();
            _deletedDocuments = new ConcurrentDictionary<int, Document>();
            _documentListSource = new SourceCache<Document, Guid>(d => d.LocalId);

            _localTextUnitId = 1;
            _addedTextUnits = new ConcurrentDictionary<int, TextUnit>();
            _changedTextUnits = new ConcurrentDictionary<int, TextUnit>();
            _deletedTextUnits = new ConcurrentDictionary<int, TextUnit>();
            _textUnitListSource = new SourceCache<TextUnit, Guid>(t => t.LocalId);

            _localNoteId = 1;
            _addedNotes = new ConcurrentDictionary<int, Note>();
            _changedNotes = new ConcurrentDictionary<int, Note>();
            _deletedNotes = new ConcurrentDictionary<int, Note>();
            _noteListSource = new SourceCache<Note, Guid>(n => n.LocalId);

            _localSourceId = 1;
            _addedContentSources = new ConcurrentDictionary<int, ContentSource>();
            _changedContentSources = new ConcurrentDictionary<int, ContentSource>();
            _deletedContentSources = new ConcurrentDictionary<int, ContentSource>();
            _contentSourceListSource = new SourceCache<ContentSource, Guid>(n => n.LocalId);

            _localLanguageId = 1;
            _addedLanguages = new ConcurrentDictionary<int, Language>();
            _changedLanguages = new ConcurrentDictionary<int, Language>();
            _deletedLanguages = new ConcurrentDictionary<int, Language>();
            _languageListSource = new SourceCache<Language, Guid>(d => d.LocalId);

            GetLanguageEntries()
                .ToObservable()
                .Subscribe(l =>
                {
                    _languageListSource.AddOrUpdate(l);
                },
                e => { /* error */ },
                () => { /* success */
                    if (_languageListSource.Items.Any())
                        _localLanguageId = _languageListSource.Items.Select(n => n.Id).DefaultIfEmpty(0).Max() + 1;
                    _languageListSource
                        .Connect()
                        .OnItemAdded(l => {
                            while (_isSaving)
                                Thread.Sleep(300);
                            if (l.Id == 0)
                                l.Id = _localLanguageId++;
                            if (Languages != null && Languages.Find(l.Id) == null)
                            {
                                Languages.Add(l);
                                _addedLanguages.TryAdd(l.Id, l);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                            }
                        })
                        .OnItemRemoved(l => {
                            if (Languages != null && Languages.Find(l.Id) != null)
                            {
                                while (_isSaving)
                                    Thread.Sleep(300);
                                if (_addedLanguages.ContainsKey(l.Id))
                                {
                                    _addedLanguages.TryRemove(l.Id, out var _);
                                    var isSaved = HasChanges();
                                    if (isSaved != _isSaved)
                                    {
                                        _isSaved = isSaved;
                                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                    }
                                } else {
                                    _deletedLanguages.TryAdd(l.Id, l);
                                    if (_isSaved)
                                    {
                                        _isSaved = false;
                                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                    }
                                }
                                Languages.Remove(l);
                                _updateTimer.Interval = 3000;
                            }
                        })
                        .DisposeMany()
                        .Subscribe();
                    _languageListSource
                        .Connect()
                        .WhenAnyPropertyChanged(new[] { nameof(Language.Name), nameof(Language.OrderNr) })
                        .Do(l => {
                            if (l == null)
                                return;
                            while (_isSaving)
                                Thread.Sleep(300);
                            if (!_addedLanguages.ContainsKey(l.Id))
                            {
                                _changedLanguages.TryAdd(l.Id, l);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                            }
                        })
                        .Subscribe();
                    }
                );

            GetDocumentEntries()
                .ToObservable()
                .Subscribe(d =>
                {
                    d.InitializeDataSources(_noteListSource, _textUnitListSource, _contentSourceListSource);
                    _documentListSource.AddOrUpdate(d);
                },
                e => { /* error */ },
                () => { /* success */
                    if (_documentListSource.Items.Any())
                        _localDocumentId = _documentListSource.Items.Select(n => n.Id).DefaultIfEmpty(0).Max() + 1;
                    _documentListSource
                        .Connect()
                        .OnItemAdded(d => {
                            while (_isSaving)
                                Thread.Sleep(300);
                            if (d.Id == 0)
                                d.Id = _localDocumentId++;
                            if (Documents != null && Documents.Find(d.Id) == null)
                            {
                                Documents.Add(d);
                                _addedDocuments.TryAdd(d.Id, d);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                                // add initial textunit when a new document is created
                                _textUnitListSource.AddOrUpdate(new TextUnit(d));
                            }
                        })
                        .OnItemRemoved(d => {
                            if (Documents != null && Documents.Find(d.Id) != null)
                            {
                                while (_isSaving)
                                    Thread.Sleep(300);
                                if (_addedDocuments.ContainsKey(d.Id))
                                {
                                    _addedDocuments.TryRemove(d.Id, out var _);
                                    var isSaved = HasChanges();
                                    if (isSaved != _isSaved)
                                    {
                                        _isSaved = isSaved;
                                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                    }
                                } else {
                                    _deletedDocuments.TryAdd(d.Id, d);
                                    if (_isSaved)
                                    {
                                        _isSaved = false;
                                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                    }
                                }
                                Documents.Remove(d);
                                _updateTimer.Interval = 3000;
                            }
                        })
                        .DisposeMany()
                        .Subscribe();
                    _documentListSource
                        .Connect()
                        .WhenAnyPropertyChanged(new[] { nameof(Document.Title), nameof(Document.ModificationDate) })
                        .Do(d => {
                            if (d == null)
                                return;
                            while (_isSaving)
                                Thread.Sleep(300);
                            if (!_addedDocuments.ContainsKey(d.Id))
                            {
                                _changedDocuments.TryAdd(d.Id, d);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                            }
                        })
                        .Subscribe();
                    }
                );

            GetTextUnitEntries()
                .ToObservable()
                .Subscribe(t =>
                {
                    t.InitializeDataSources(_noteListSource, _textUnitListSource);
                    _textUnitListSource.AddOrUpdate(t);
                },
                e => { /* error */ },
                () => { /* success */
                    if (_textUnitListSource.Items.Any())
                    {
                        InitializeOrderNumbers();

                        _localTextUnitId = _textUnitListSource.Items.Select(t => t.Id).DefaultIfEmpty(0).Max() + 1;

                        foreach (var textUnit in _textUnitListSource.Items)
                        {
                            if (textUnit.Successor != null)
                                textUnit.Successor.Predecessor = textUnit;
                        }
                    }
                    _textUnitListSource
                        .Connect()
                        .OnItemAdded(t => {
                            while (_isSaving)
                                Thread.Sleep(300);
                            if (t.Id == 0)
                                t.Id = _localTextUnitId++;
                            if (TextUnits != null && TextUnits.Find(t.Id) == null)
                            {
                                TextUnits.Add(t);
                                _addedTextUnits.TryAdd(t.Id, t);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                                // add initial note when a new textunit is created
                                var initialNote = new Note
                                {
                                    RelatedTextUnit = t,
                                    RelatedTextUnitId = t.Id
                                };
                                _noteListSource.AddOrUpdate(initialNote);
                            }
                        })
                        .OnItemRemoved(t => {
                            if (TextUnits != null && TextUnits.Find(t.Id) != null)
                            {
                                while (_isSaving)
                                    Thread.Sleep(300);
                                if (_addedTextUnits.ContainsKey(t.Id))
                                {
                                    _addedTextUnits.TryRemove(t.Id, out var _);
                                    var isSaved = HasChanges();
                                    if (isSaved != _isSaved)
                                    {
                                        _isSaved = isSaved;
                                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                    }
                                } else {
                                    _deletedTextUnits.TryAdd(t.Id, t);
                                    if (_isSaved)
                                    {
                                        _isSaved = false;
                                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                    }
                                }
                                TextUnits.Remove(t);
                                _updateTimer.Interval = 3000;
                            }
                        })
                        .DisposeMany()
                        .Subscribe();
                    _textUnitListSource
                        .Connect()
                        .WhenAnyPropertyChanged(new[] { nameof(TextUnit.ModificationDate) })
                        .Do(t => {
                            if (t == null)
                                return;
                            while (_isSaving)
                                Thread.Sleep(300);
                            if (!_addedTextUnits.ContainsKey(t.Id))
                            {
                                _changedTextUnits.TryAdd(t.Id, t);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                            }
                        })
                        .Subscribe();
                    }
                );

            GetNoteEntries()
                .ToObservable()
                .Subscribe(n =>
                { _noteListSource.AddOrUpdate(n); },
                e => { /* error */ },
                () => { /* success */
                    if (_noteListSource.Items.Any())
                        _localNoteId = _noteListSource.Items.Select(n => n.Id).DefaultIfEmpty(0).Max() + 1;
                    _noteListSource
                        .Connect()
                        .OnItemAdded(n => {
                            if (n == null)
                                return;
                            while (_isSaving)
                                Thread.Sleep(300);
                            if (n.Id == 0)
                                n.Id = _localNoteId++;
                            n.IsReadonly = n.DerivedNotes?.Any() == true;
                            if (Notes != null && Notes.Find(n.Id) == null)
                            {
                                Notes.Add(n);
                                _addedNotes.TryAdd(n.Id, n);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                            }
                        })
                        .OnItemRemoved(n => {
                            if (Notes != null && Notes.Find(n.Id) != null)
                            {
                                while (_isSaving)
                                    Thread.Sleep(300);
                                if (_addedNotes.ContainsKey(n.Id))
                                {
                                    _addedNotes.TryRemove(n.Id, out var _);
                                    var isSaved = HasChanges();
                                    if (isSaved != _isSaved)
                                    {
                                        _isSaved = isSaved;
                                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                    }
                                } else {
                                    _deletedNotes.TryAdd(n.Id, n);
                                    if (_isSaved)
                                    {
                                        _isSaved = false;
                                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                    }
                                }
                                Notes.Remove(n);
                                _updateTimer.Interval = 3000;
                            }
                        })
                        .DisposeMany()
                        .Subscribe();
                    _noteListSource
                        .Connect()
                        .WhenAnyPropertyChanged(new[] { nameof(Note.Text), nameof(Note.LanguageId) })
                        .Do(n => {
                            if (n == null)
                                return;
                            while (_isSaving)
                                Thread.Sleep(300);
                            if (!_addedNotes.ContainsKey(n.Id))
                            {
                                _changedNotes.TryAdd(n.Id, n);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                            }
                        })
                        .Subscribe();
                }
            );

            GetContentSourceEntries()
                .ToObservable()
                .Subscribe(cs =>
                { _contentSourceListSource.AddOrUpdate(cs); },
                e => { /* error */ },
                () => { /* success */
                    if (_contentSourceListSource.Items.Any())
                        _localSourceId = _contentSourceListSource.Items.Select(s => s.Id).DefaultIfEmpty(0).Max() + 1;
                    _contentSourceListSource
                        .Connect()
                        .OnItemAdded(cs => {
                            while (_isSaving)
                                Thread.Sleep(300);
                            if (cs.Id == 0)
                                cs.Id = _localSourceId++;
                            if (ContentSources != null && ContentSources.Find(cs.Id) == null)
                            {
                                ContentSources.Add(cs);
                                _addedContentSources.TryAdd(cs.Id, cs);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                            }
                        })
                        .OnItemRemoved(cs => {
                            if (ContentSources != null && ContentSources.Find(cs.Id) != null)
                            {
                                while (_isSaving)
                                    Thread.Sleep(300);
                                if (_addedContentSources.ContainsKey(cs.Id))
                                {
                                    _addedContentSources.TryRemove(cs.Id, out var _);
                                    var isSaved = HasChanges();
                                    if (isSaved != _isSaved)
                                    {
                                        _isSaved = isSaved;
                                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                    }
                                } else {
                                    _deletedContentSources.TryAdd(cs.Id, cs);
                                    if (_isSaved)
                                    {
                                        _isSaved = false;
                                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                    }
                                }
                                ContentSources.Remove(cs);
                                _updateTimer.Interval = 3000;
                            }
                        })
                        .DisposeMany()
                        .Subscribe();
                    _contentSourceListSource
                        .Connect()
                        .WhenAnyPropertyChanged(new[] { nameof(ContentSource.Author), nameof(ContentSource.Title), nameof(ContentSource.Chapter), nameof(ContentSource.PageNumber), nameof(ContentSource.Url), nameof(ContentSource.Timestamp) })
                        .Do(cs => {
                            if (cs == null)
                                return;
                            while (_isSaving)
                                Thread.Sleep(300);
                            if (!_addedContentSources.ContainsKey(cs.Id))
                            {
                                _changedContentSources.TryAdd(cs.Id, cs);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                            }
                        })
                        .Subscribe();
                }
            );
        }

        void InitializeOrderNumbers()
        {
            var documentsContent = _textUnitListSource.Items.GroupBy(t => t.RelatedDocumentId);
            foreach (var documentTextUnits in documentsContent)
            {
                var documentOrderNr = 0;
                var rootLevelTextUnits = documentTextUnits.Where(t => t.ParentId == null).ToList();
                SetOrderNrR(GetSortedList(rootLevelTextUnits), ref documentOrderNr);
            }
        }

        void SetOrderNrR(List<TextUnit> sortedLocalLevelGroupTextUnits, ref int currentOrderNr)
        {
            foreach (var currentTextUnit in sortedLocalLevelGroupTextUnits)
            {
                currentTextUnit.OrderNr = currentOrderNr++;
                if (currentTextUnit.TextUnitChildList != null)
                    SetOrderNrR(GetSortedList(currentTextUnit.TextUnitChildList), ref currentOrderNr);
            }
        }

        static List<TextUnit> GetSortedList(List<TextUnit> localLevelGroupTextUnits)
        {
            var sortedList = new List<TextUnit>();
            var currentElement = localLevelGroupTextUnits.FirstOrDefault(t => t.SuccessorId == null);
            while(currentElement != null)
            {
                sortedList.Insert(0, currentElement);
                currentElement = localLevelGroupTextUnits.FirstOrDefault(t => t.SuccessorId == currentElement.Id);
            }
            return sortedList;
        }


        private bool HasChanges()
        {
            if (_addedNotes?.Any() == true ||
                _changedNotes?.Any() == true ||
                _deletedNotes?.Any() == true ||
                _changedTextUnits?.Any() == true ||
                _deletedTextUnits?.Any() == true ||
                _changedDocuments?.Any() == true ||
                _deletedDocuments?.Any() == true)
                return true;
            return false;
        }

        private void OnUpdateTimerElapsedEvent(object? sender, ElapsedEventArgs e)
        {
            if (!_isSaved && !_isSaving)
            {
                _isSaving = true;

                if (_addedDocuments?.Any() == true)
                {
                    _addedDocuments.Clear();
                }
                if (Documents != null)
                {
                    if (_changedDocuments?.Any() == true)
                    {
                        Documents.UpdateRange(_changedDocuments.Values);
                        _changedDocuments.Clear();
                    }
                    if (_deletedDocuments?.Any() == true)
                    {
                        Documents.RemoveRange(_deletedDocuments.Values);
                        _deletedDocuments.Clear();
                    }
                }

                if (_addedTextUnits?.Any() == true)
                {
                    _addedTextUnits.Clear();
                }
                if (TextUnits != null)
                {
                    if (_changedTextUnits?.Any() == true)
                    {
                        TextUnits.UpdateRange(_changedTextUnits.Values);
                        _changedTextUnits.Clear();
                    }
                    if (_deletedTextUnits?.Any() == true)
                    {
                        TextUnits.RemoveRange(_deletedTextUnits.Values);
                        _deletedTextUnits.Clear();
                    }
                }

                if (_addedNotes?.Any() == true)
                {
                    _addedNotes.Clear();
                }
                if (Notes != null)
                {
                    if (_changedNotes?.Any() == true)
                    {
                        Notes.UpdateRange(_changedNotes.Values);
                        _changedNotes.Clear();
                    }
                    if (_deletedNotes?.Any() == true)
                    {
                        Notes.RemoveRange(_deletedNotes.Values);
                        _deletedNotes.Clear();
                    }
                }

                if (_addedContentSources?.Any() == true)
                {
                    _addedContentSources.Clear();
                }
                if (ContentSources != null)
                {
                    if (_changedContentSources?.Any() == true)
                    {
                        ContentSources.UpdateRange(_changedContentSources.Values);
                        _changedContentSources.Clear();
                    }
                    if (_deletedContentSources?.Any() == true)
                    {
                        ContentSources.RemoveRange(_deletedContentSources.Values);
                        _deletedContentSources.Clear();
                    }
                }

                if (_addedLanguages?.Any() == true)
                {
                    _addedLanguages.Clear();
                }
                if (Languages != null)
                {
                    if (_changedLanguages?.Any() == true)
                    {
                        Languages.UpdateRange(_changedLanguages.Values);
                        _changedLanguages.Clear();
                    }
                    if (_deletedLanguages?.Any() == true)
                    {
                        Languages.RemoveRange(_deletedLanguages.Values);
                        _deletedLanguages.Clear();
                    }
                }

                SaveChanges();
                _isSaved = true;
                _eventAggregator.Publish(new NotifySaveStateChanged(false));

                _isSaving = false;
            }
        }

        #region Database Access Functions

        private IEnumerable<Language> GetLanguageEntries()
        {
            if (Languages == null)
                yield break;
            IQueryable dbLanguageQuery = Languages;
            foreach (Language dbLanguage in dbLanguageQuery)
                yield return dbLanguage;
        }

        private IEnumerable<ContentSource> GetContentSourceEntries()
        {
            if (ContentSources == null)
                yield break;
            IQueryable dbContentSourcesQuery = ContentSources;
            foreach (ContentSource dbContentSource in dbContentSourcesQuery)
                yield return dbContentSource;
        }

        private IEnumerable<Note> GetNoteEntries()
        {
            if (Notes == null)
                yield break;
            IQueryable dbNotesQuery = Notes.Include(n => n.DerivedNotes).Include(n => n.SourceNotes).OrderByDescending(n => n.ModificationDate);
            foreach (Note dbNote in dbNotesQuery)
                yield return dbNote;
        }

        private IEnumerable<TextUnit> GetTextUnitEntries()
        {
            if (TextUnits == null)
                yield break;
            IQueryable dbTextUnitsQuery = TextUnits.OrderByDescending(t => t.ModificationDate);
            foreach (TextUnit dbTextUnit in dbTextUnitsQuery)
                yield return dbTextUnit;
        }

        private IEnumerable<Document> GetDocumentEntries()
        {
            if (Documents == null)
                yield break;
            IQueryable dbDocumentsQuery = Documents.OrderByDescending(d => d.ModificationDate);
            foreach (Document dbDocument in dbDocumentsQuery)
                yield return dbDocument;
        }

        #endregion

        #region Public Properties

        public SourceCache<Document, Guid> DocumentListSource => _documentListSource;
        public SourceCache<TextUnit, Guid> TextUnitListSource => _textUnitListSource;
        public SourceCache<Note, Guid> NoteListSource => _noteListSource;
        public SourceCache<ContentSource, Guid> ContentSourceListSource => _contentSourceListSource;
        public SourceCache<Language, Guid> LanguageListSource => _languageListSource;

        #endregion
    }
}
