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

        private int _localNoteId;
        private readonly ConcurrentDictionary<int, Note> _changedNotes;
        private readonly SourceCache<Note, int> _noteListSource;

        private int _localTextUnitId;
        private readonly ConcurrentDictionary<int, TextUnit> _changedTextUnits;
        private readonly SourceCache<TextUnit, int> _textUnitListSource;

        private int _localDocumentId;
        private readonly ConcurrentDictionary<int, Document> _changedDocuments;
        private readonly SourceCache<Document, int> _documentListSource;

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

            _localNoteId = 1;
            _localTextUnitId = 1;
            _localDocumentId = 1;

            _changedNotes = new ConcurrentDictionary<int, Note>();
            _noteListSource = new SourceCache<Note, int>(n => n.Id);
            _noteListSource
                .Connect()
                .OnItemAdded(n => {
                    if (n.Id == 0)
                        n.Id = _localNoteId++;
                    if (Notes.Find(n.Id) == null)
                    {
                        Notes.Add(n);
                        SaveChanges();
                    }})
                .OnItemRemoved(n => { 
                    if (Notes.Find(n.Id) != null) 
                        Notes.Remove(n); })
                .DisposeMany()
                .Subscribe(); 
            _noteListSource
                .Connect()
                .WhenAnyPropertyChanged(new[] { nameof(Note.Text) })
                .Do(n => {
                    _changedNotes.TryAdd(n.Id, n);
                    _updateTimer.Interval = 3000;
                    if (_isSaved)
                    {
                        _isSaved = false;
                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                    }
                })
                .Subscribe();
            GetNoteEntries()
                .ToObservable()
                .Subscribe(n => 
                    { _noteListSource.AddOrUpdate(n); },
                    e => { /* error */ },
                    () => { /* success */
                        if (_noteListSource.Items.Count() > 0)
                            _localNoteId = _noteListSource.Items.Max(n => n.Id) + 1;
                    }
                );

            _changedTextUnits = new ConcurrentDictionary<int, TextUnit>();
            _textUnitListSource = new SourceCache<TextUnit, int>(t => t.Id);
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
                        TextUnits.Remove(t);
                })
                .DisposeMany()
                .Subscribe();
            _textUnitListSource
                .Connect()
                .WhenAnyPropertyChanged(new[] { nameof(Note.Text) })
                .Do(t => {
                    _changedTextUnits.TryAdd(t.Id, t);
                    _updateTimer.Interval = 3000;
                    if (_isSaved)
                    {
                        _isSaved = false;
                        _eventAggregator.Publish(new NotifySaveStateChanged(true));
                    }
                })
                .Subscribe();
            GetTextUnitEntries()
                .ToObservable()
                .Subscribe(t =>
                { _textUnitListSource.AddOrUpdate(t); },
                    e => { /* error */ },
                    () => { /* success */
                        if (_textUnitListSource.Items.Count() > 0)
                            _localTextUnitId = _textUnitListSource.Items.Max(t => t.Id) + 1;
                    }
                );

            _changedDocuments = new ConcurrentDictionary<int, Document>();
            _documentListSource = new SourceCache<Document, int>(d => d.Id);
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
                        Documents.Remove(d);
                })
                .DisposeMany()
                .Subscribe();
            _documentListSource
               .Connect()
               .WhenAnyPropertyChanged(new[] { nameof(Document.Title), nameof(Document.TextUnitList) /*nameof(Document.ModificationDate)*/ })
               .Do(d => {
                   _changedDocuments.TryAdd(d.Id, d);
                   _updateTimer.Interval = 3000;
                   if (_isSaved)
                   {
                       _isSaved = false;
                       _eventAggregator.Publish(new NotifySaveStateChanged(true));
                   }
               })
               .Subscribe();
            GetDocumentEntries()
                .ToObservable()
                .Subscribe(d =>
                { _documentListSource.AddOrUpdate(d); },
                    e => { /* error */ },
                    () => { /* success */
                        if (_documentListSource.Items.Count() > 0)
                            _localDocumentId = _documentListSource.Items.Max(n => n.Id) + 1;
                    }
                );
        }

        private void OnUpdateTimerElapsedEvent(object sender, ElapsedEventArgs e)
        {
            if (_changedNotes?.Count > 0)
            {
                Notes.UpdateRange(_changedNotes.Values);
                _changedNotes.Clear();
            }
            if (_changedDocuments?.Count > 0)
            {
                Documents.UpdateRange(_changedDocuments.Values);
                _changedDocuments.Clear();
            }
            if (!_isSaved)
            {
                SaveChanges();
                _isSaved = true;
                _eventAggregator.Publish(new NotifySaveStateChanged(false));
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
            IQueryable dbDocumentsQuery = Documents.OrderByDescending(d => d.ModificationDate)
                .Include(d => d.TextUnitList)
                .ThenInclude(tu => tu.NoteList);
            foreach (Document dbDocument in dbDocumentsQuery)
                yield return dbDocument;
        }

        #endregion

        #region Public Properties

        public SourceCache<Note, int> NoteListSource => _noteListSource;
        public SourceCache<TextUnit, int> TextUnitListSource => _textUnitListSource;
        public SourceCache<Document, int> DocumentListSource => _documentListSource;

        #endregion
    }
}
