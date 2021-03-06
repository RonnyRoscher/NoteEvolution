using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reactive.Linq;
using DynamicData;
using ReactiveUI;

namespace NoteEvolution.Models
{
    public class Document : ReactiveObject
    {
        #region Private Properties

        private SourceCache<Note, Guid> _globalNoteListSource;
        private IObservable<IChangeSet<Note, Guid>> _noteListSource;

        private SourceCache<TextUnit, Guid> _globalTextUnitListSource;
        private IObservable<IChangeSet<TextUnit, Guid>> _textUnitListSource;

        private SourceCache<ContentSource, Guid> _globalContentSourceListSource;

        #endregion

        public Document()
        {
            Id = 0;
            LocalId = Guid.NewGuid();

            InitializeDataSources(new SourceCache<Note, Guid>(n => n.LocalId), new SourceCache<TextUnit, Guid>(t => t.LocalId), new SourceCache<ContentSource, Guid>(t => t.LocalId));
        }

        public Document(SourceCache<Note, Guid> globalNoteListSource, SourceCache<TextUnit, Guid> globalTextUnitListSource, SourceCache<ContentSource, Guid> contentSourceListSource)
        {
            CreationDate = DateTime.Now;
            Id = 0;
            LocalId = Guid.NewGuid();
            CreationDate = DateTime.Now;
            ModificationDate = DateTime.Now;

            InitializeDataSources(globalNoteListSource, globalTextUnitListSource, contentSourceListSource);

            // add initial textunit when a new document is created (detected by it not having related textunits yet)
            if (TextUnitList.FirstOrDefault() == null)
                GlobalTextUnitListSource.AddOrUpdate(new TextUnit(this));
        }

        public void InitializeDataSources(SourceCache<Note, Guid> globalNoteListSource, SourceCache<TextUnit, Guid> globalTextUnitListSource, SourceCache<ContentSource, Guid> contentSourceListSource)
        {
            // only set if not already set previously
            //if (_globalNoteListSource?.Items.Any() != true)
            {
                _globalNoteListSource = globalNoteListSource;
                _noteListSource = _globalNoteListSource
                    .Connect()
                    .Filter(n => n.RelatedTextUnit?.RelatedDocumentId == Id);

                _globalTextUnitListSource = globalTextUnitListSource;
                _textUnitListSource = _globalTextUnitListSource
                    .Connect()
                    .Filter(t => t.RelatedDocumentId == Id);

                // update ModifiedDate on changes to local text unit properties
                this.WhenAnyValue(d => d.CreationDate, d => d.Title)
                    .Select(_ => DateTime.Now)
                    .Do(d => ModificationDate = d);

                // load associated textunits from db and keep updated when adding and deleting textunits, as well as update ModifiedDate on ModifiedDate changes in any associated textunit
                TextUnitList = new List<TextUnit>();
                _textUnitListSource
                    .OnItemAdded(t => { TextUnitList.Add(t); })
                    .OnItemRemoved(t => { TextUnitList.Remove(t); })
                    .WhenPropertyChanged(t => t.ModificationDate)
                    .Throttle(TimeSpan.FromMilliseconds(250))
                    .Select(t => t.Value)
                    .Do(d => ModificationDate = d);

                _globalContentSourceListSource = contentSourceListSource;
            }
        }

        #region Public Methods

        // todo: AddExisting & CreateNew
        public TextUnit AddTextUnit()
        {
            var latestRootTextUnit = TextUnitList.LastOrDefault();
            if (latestRootTextUnit != null)
                return latestRootTextUnit.AddSuccessor();
            return null;
        }

        public void RemoveTextUnit(TextUnit oldTextUnit)
        {
            if (oldTextUnit != null)
                _globalTextUnitListSource.Remove(oldTextUnit);
        }

        public void DeleteTextUnit(TextUnit oldTextUnit)
        {
            if (oldTextUnit != null)
            {
                if (oldTextUnit.NoteList.Count > 0)
                    _globalNoteListSource.Remove(oldTextUnit.NoteList.ToList());
                _globalTextUnitListSource.Remove(oldTextUnit);
            }
        }

        #endregion

        #region Public Properties

        private int _id;

        [Key]
        public int Id
        {
            get => _id;
            set => this.RaiseAndSetIfChanged(ref _id, value);
        }

        private Guid _localId;

        [NotMapped]
        public Guid LocalId
        {
            get => _localId;
            set => this.RaiseAndSetIfChanged(ref _localId, value);
        }

        private DateTime _creationDate;

        public DateTime CreationDate
        {
            get => _creationDate;
            set => this.RaiseAndSetIfChanged(ref _creationDate, value);
        }

        private DateTime _modificationDate;

        public DateTime ModificationDate
        {
            get => _modificationDate;
            set => this.RaiseAndSetIfChanged(ref _modificationDate, value);
        }

        private string _title;

        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        private List<TextUnit> _textUnitList;

        public virtual List<TextUnit> TextUnitList
        {
            get => _textUnitList;
            set => this.RaiseAndSetIfChanged(ref _textUnitList, value);
        }

        [NotMapped]
        public IObservable<IChangeSet<Note, Guid>> NoteListSource => _noteListSource;

        [NotMapped]
        public SourceCache<Note, Guid> GlobalNoteListSource => _globalNoteListSource;

        [NotMapped]
        public IObservable<IChangeSet<TextUnit, Guid>> TextUnitListSource => _textUnitListSource;

        [NotMapped]
        public SourceCache<TextUnit, Guid> GlobalTextUnitListSource => _globalTextUnitListSource;

        [NotMapped]
        public SourceCache<ContentSource, Guid> GlobalContentSourceListSource => _globalContentSourceListSource;

        #endregion
    }
}
