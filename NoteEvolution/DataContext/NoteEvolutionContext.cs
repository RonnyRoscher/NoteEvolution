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

        #endregion

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TextUnit>()
                .HasMany(parent => parent.TextUnitChildList)
                .WithOne(child => child.Parent)
                .HasForeignKey(child => child.ParentId);

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

            GetDocumentEntries()
                .ToObservable()
                .Subscribe(d =>
                {
                    d.InitializeDataSources(_noteListSource, _textUnitListSource);
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
                        .WhenAnyPropertyChanged(new[] { nameof(Document.Title), nameof(Document.TextUnitList) /*nameof(Document.ModificationDate)*/ })
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
                                t.Id = _localTextUnitId++;
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
                        .WhenAnyPropertyChanged(new[] { nameof(Note.Text) })
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
                                _deletedNotes.TryAdd(n.Id, n);
                                Notes.Remove(n);
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

                // required to prevent crash on immediate text change before it was saved
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

                SaveChanges();
                _isSaved = true;
                _eventAggregator.Publish(new NotifySaveStateChanged(false));

                _isSaving = false;
            }
        }

        #region Database Access Functions

        private IEnumerable<Note> GetNoteEntries()
        {
            IQueryable dbNotesQuery = Notes.OrderByDescending(n => n.ModificationDate);
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

        public SourceCache<Note, Guid> NoteListSource => _noteListSource;
        public SourceCache<TextUnit, Guid> TextUnitListSource => _textUnitListSource;
        public SourceCache<Document, Guid> DocumentListSource => _documentListSource;

        #endregion
    }
}
