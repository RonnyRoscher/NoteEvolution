using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Timers;
using Microsoft.EntityFrameworkCore;
using DynamicData;
using PubSub;
using NoteEvolution.Events;
using NoteEvolution.Models;

namespace NoteEvolution.DataContext
{
    public class NoteEvolutionContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite("Data Source=local.db");

        #region Virtual Database Tables

        public virtual DbSet<Document> Documents { get; set; }
        public virtual DbSet<TextUnit> TextUnits { get; set; }
        public virtual DbSet<Note> Notes { get; set; }
        public virtual DbSet<ContentSource> ContentSources { get; set; }

        #endregion

        #region Private Members

        private readonly Hub _eventAggregator;

        private Timer _updateTimer;
        private bool _isSaved;
        private bool _isSaving;

        private int _localDocumentId;
        private readonly ConcurrentDictionary<int, Document> _changedDocuments;
        private readonly ConcurrentDictionary<int, Document> _deletedDocuments;
        private readonly SourceCache<Document, Guid> _documentListSource;

        private int _localTextUnitId;
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

        public NoteEvolutionContext()
        {
            Database.EnsureCreated();

            _eventAggregator = Hub.Default;

            _updateTimer = new Timer(3000);
            _updateTimer.Elapsed += OnUpdateTimerElapsedEvent;
            _updateTimer.AutoReset = true;
            _updateTimer.Enabled = true;

            _isSaved = true;
            _isSaving = false;

            _localDocumentId = 1;
            _changedDocuments = new ConcurrentDictionary<int, Document>();
            _deletedDocuments = new ConcurrentDictionary<int, Document>();
            _documentListSource = new SourceCache<Document, Guid>(d => d.LocalId);

            _localTextUnitId = 1;
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

            GetDocumentEntries()
                .ToObservable()
                .Subscribe(d =>
                {
                    d.InitializeDataSources(_noteListSource, _textUnitListSource, _contentSourceListSource);
                    _documentListSource.AddOrUpdate(d);
                },
                e => { /* error */ },
                () => { /* success */
                    if (_documentListSource.Items.Count() > 0)
                        _localDocumentId = _documentListSource.Items.Max(n => n.Id) + 1;
                    _documentListSource
                        .Connect()
                        .OnItemAdded(d => {
                            if (d.Id == 0)
                                d.Id = _localDocumentId++;
                            if (Documents.Find(d.Id) == null)
                            {
                                Documents.Add(d);
                                SaveChanges();
                            }
                        })
                        .OnItemRemoved(d => {
                            if (Documents.Find(d.Id) != null)
                            {
                                while (_isSaving)
                                    System.Threading.Thread.Sleep(300);
                                _deletedDocuments.TryAdd(d.Id, d);
                                Documents.Remove(d);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                            }
                        })
                        .DisposeMany()
                        .Subscribe();
                    _documentListSource
                        .Connect()
                        .WhenAnyPropertyChanged(new[] { nameof(Document.Title), nameof(Document.TextUnitList), nameof(Document.ModificationDate) })
                        .Do(d => {
                            while (_isSaving)
                                System.Threading.Thread.Sleep(300);
                            _changedDocuments.TryAdd(d.Id, d);
                            _updateTimer.Interval = 3000;
                            if (_isSaved)
                            {
                                _isSaved = false;
                                _eventAggregator.Publish(new NotifySaveStateChanged(true));
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
                    if (_textUnitListSource.Items.Count() > 0)
                    {
                        InitializeOrderNumbers();

                        _localTextUnitId = _textUnitListSource.Items.Max(t => t.Id) + 1;

                        foreach (var textUnit in _textUnitListSource.Items)
                        {
                            if (textUnit.Successor != null)
                                textUnit.Successor.Predecessor = textUnit;
                        }
                    }
                    _textUnitListSource
                        .Connect()
                        .OnItemAdded(t => {
                            if (t.Id == 0)
                            {
                                t.Id = _localTextUnitId++;
                                // add initial note 
                                var initialNote = new Note();
                                initialNote.RelatedTextUnit = t;
                                initialNote.RelatedTextUnitId = t.Id;
                                _noteListSource.AddOrUpdate(initialNote);
                            }
                            if (TextUnits.Find(t.Id) == null)
                            {
                                TextUnits.Add(t);
                                SaveChanges();
                            }
                        })
                        .OnItemRemoved(t => {
                            if (TextUnits.Find(t.Id) != null)
                            {
                                while (_isSaving)
                                    System.Threading.Thread.Sleep(300);
                                _deletedTextUnits.TryAdd(t.Id, t);
                                TextUnits.Remove(t);
                                _updateTimer.Interval = 3000;
                                if (_isSaved)
                                {
                                    _isSaved = false;
                                    _eventAggregator.Publish(new NotifySaveStateChanged(true));
                                }
                            }
                        })
                        .DisposeMany()
                        .Subscribe();
                    _textUnitListSource
                        .Connect()
                        .WhenAnyPropertyChanged(new[] { nameof(TextUnit.ModificationDate) })
                        .Do(t => {
                            while (_isSaving)
                                System.Threading.Thread.Sleep(300);
                            _changedTextUnits.TryAdd(t.Id, t);
                            _updateTimer.Interval = 3000;
                            if (_isSaved)
                            {
                                _isSaved = false;
                                _eventAggregator.Publish(new NotifySaveStateChanged(true));
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
                    if (_noteListSource.Items.Count() > 0)
                        _localNoteId = _noteListSource.Items.Max(n => n.Id) + 1;
                    _noteListSource
                        .Connect()
                        .OnItemAdded(n => {
                            while (_isSaving)
                                System.Threading.Thread.Sleep(300);
                            if (n.Id == 0)
                                n.Id = _localNoteId++;
                            n.IsReadonly = n.DerivedNotes.Any();
                            if (Notes.Find(n.Id) == null)
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
                            if (Notes.Find(n.Id) != null)
                            {
                                while (_isSaving)
                                    System.Threading.Thread.Sleep(300);
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
                        .WhenAnyPropertyChanged(new[] { nameof(Note.Text) })
                        .Do(n => {
                            while (_isSaving)
                                System.Threading.Thread.Sleep(300);
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
                    if (_contentSourceListSource.Items.Count() > 0)
                        _localSourceId = _contentSourceListSource.Items.Max(s => s.Id) + 1;
                    _contentSourceListSource
                        .Connect()
                        .OnItemAdded(cs => {
                            while (_isSaving)
                                System.Threading.Thread.Sleep(300);
                            if (cs.Id == 0)
                                cs.Id = _localSourceId++;
                            if (ContentSources.Find(cs.Id) == null)
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
                            if (ContentSources.Find(cs.Id) != null)
                            {
                                while (_isSaving)
                                    System.Threading.Thread.Sleep(300);
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
                            while (_isSaving)
                                System.Threading.Thread.Sleep(300);
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

        void SetOrderNrR(List<TextUnit> sortedLocalLevelGroupTextUnits, ref int currentOrderNr, int? parentId = null)
        {
            foreach (var currentTextUnit in sortedLocalLevelGroupTextUnits)
            {
                currentTextUnit.OrderNr = currentOrderNr++;
                SetOrderNrR(GetSortedList(currentTextUnit.TextUnitChildList), ref currentOrderNr, currentTextUnit.Id);
            }
        }

        List<TextUnit> GetSortedList(List<TextUnit> localLevelGroupTextUnits)
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
            if (_addedNotes?.Count > 0 ||
                _changedNotes?.Count > 0 ||
                _deletedNotes?.Count > 0 ||
                _changedTextUnits?.Count > 0 ||
                _deletedTextUnits?.Count > 0 ||
                _changedDocuments?.Count > 0 ||
                _deletedDocuments?.Count > 0)
                return true;
            return false;
        }

        private void OnUpdateTimerElapsedEvent(object sender, ElapsedEventArgs e)
        {
            if (!_isSaved && !_isSaving)
            {
                _isSaving = true;

                if (_changedDocuments?.Count > 0)
                {
                    Documents.UpdateRange(_changedDocuments.Values);
                    _changedDocuments.Clear();
                }
                if (_deletedDocuments?.Count > 0)
                {
                    Documents.RemoveRange(_deletedDocuments.Values);
                    _deletedDocuments.Clear();
                }

                if (_changedTextUnits?.Count > 0)
                {
                    TextUnits.UpdateRange(_changedTextUnits.Values);
                    _changedTextUnits.Clear();
                }
                if (_deletedTextUnits?.Count > 0)
                {
                    TextUnits.RemoveRange(_deletedTextUnits.Values);
                    _deletedTextUnits.Clear();
                }

                if (_addedNotes?.Count > 0)
                {
                    _addedNotes.Clear();
                }
                if (_changedNotes?.Count > 0)
                {
                    Notes.UpdateRange(_changedNotes.Values);
                    _changedNotes.Clear();
                }
                if (_deletedNotes?.Count > 0)
                {
                    Notes.RemoveRange(_deletedNotes.Values);
                    _deletedNotes.Clear();
                }

                if (_addedContentSources?.Count > 0)
                {
                    _addedContentSources.Clear();
                }
                if (_changedContentSources?.Count > 0)
                {
                    ContentSources.UpdateRange(_changedContentSources.Values);
                    _changedContentSources.Clear();
                }
                if (_deletedContentSources?.Count > 0)
                {
                    ContentSources.RemoveRange(_deletedContentSources.Values);
                    _deletedContentSources.Clear();
                }

                SaveChanges();
                _isSaved = true;
                _eventAggregator.Publish(new NotifySaveStateChanged(false));

                _isSaving = false;
            }
        }

        #region Database Access Functions

        private IEnumerable<ContentSource> GetContentSourceEntries()
        {
            IQueryable dbContentSourcesQuery = ContentSources;
            foreach (ContentSource dbContentSource in dbContentSourcesQuery)
                yield return dbContentSource;
        }

        private IEnumerable<Note> GetNoteEntries()
        {
            IQueryable dbNotesQuery = Notes.Include(n => n.DerivedNotes).Include(n => n.SourceNotes).OrderByDescending(n => n.ModificationDate);
            foreach (Note dbNote in dbNotesQuery)
                yield return dbNote;
        }

        private IEnumerable<TextUnit> GetTextUnitEntries()
        {
            IQueryable dbTextUnitsQuery = TextUnits.OrderByDescending(t => t.ModificationDate);
            foreach (TextUnit dbTextUnit in dbTextUnitsQuery)
                yield return dbTextUnit;
        }

        private IEnumerable<Document> GetDocumentEntries()
        {
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

        #endregion
    }
}
