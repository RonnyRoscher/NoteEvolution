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

        private SourceCache<Note, int> _globalNoteListSource;
        private readonly IObservable<IChangeSet<Note, int>> _noteListSource;

        private SourceCache<TextUnit, int> _globalTextUnitListSource;
        private readonly IObservable<IChangeSet<TextUnit, int>> _textUnitListSource;

        #endregion

        public Document()
        {
            _globalNoteListSource = new SourceCache<Note, int>(n => n.Id);
            _noteListSource = _globalNoteListSource
                .Connect()
                .Filter(n => n.RelatedTextUnit?.RelatedDocumentId == Id);

            _globalTextUnitListSource = new SourceCache<TextUnit, int>(t => t.Id);
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
        }

        public Document(SourceCache<Note, int> globalNoteListSource, SourceCache<TextUnit, int> globalTextUnitListSource)
        {
            CreationDate = DateTime.Now;

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

        #endregion

        #region Public Properties

        private int _id;

        [Key]
        public int Id
        {
            get => _id;
            set => this.RaiseAndSetIfChanged(ref _id, value);
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
        public IObservable<IChangeSet<Note, int>> NoteListSource => _noteListSource;

        [NotMapped]
        public SourceCache<Note, int> GlobalNoteListSource => _globalNoteListSource;

        [NotMapped]
        public IObservable<IChangeSet<TextUnit, int>> TextUnitListSource => _textUnitListSource;

        [NotMapped]
        public SourceCache<TextUnit, int> GlobalTextUnitListSource => _globalTextUnitListSource;

        #endregion
    }
}
