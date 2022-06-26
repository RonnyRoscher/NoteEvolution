using System;
using System.Reactive.Linq;
using System.Linq;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using ReactiveUI;
using System.Collections.ObjectModel;

namespace NoteEvolution.DAL.Models
{
    /// <summary>
    /// Class describing a simple unstructured note object.
    /// </summary>
    public class Note : ReactiveObject
    {
        private bool _modificationDateUnlocked;
        
        public Note()
        {
            _modificationDateUnlocked = true;

            Id = 0;
            LocalId = Guid.NewGuid();
            CreationDate = DateTime.Now;
            ModificationDate = DateTime.Now;
            LanguageId = 1;

            SourceNotes = new ObservableCollection<Note>();
            DerivedNotes = new ObservableCollection<Note>();
            RelatedSources = new ObservableCollection<ContentSource>();

            this.DerivedNotes.CollectionChanged += DerivedNotes_CollectionChanged;

            // update ModifiedDate on changes to local note properties
            this.WhenAnyValue(n => n.Text, n => n._modificationDateUnlocked)
                .Skip(1)
                .Where(n => n.Item2)
                .Throttle(TimeSpan.FromSeconds(0.5), RxApp.MainThreadScheduler)
                .Do(n => ModificationDate = DateTime.Now)
                .Subscribe();
        }

        private void DerivedNotes_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            IsReadonly = DerivedNotes?.Any() == true;
        }

        #region Public Methods

        public void SetValueWithoutModificationDateChange(string newValue)
        {
            _modificationDateUnlocked = false;
            Text = newValue;
            _modificationDateUnlocked = true;
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

        private byte _languageId;

        public byte LanguageId
        {
            get => _languageId;
            set => this.RaiseAndSetIfChanged(ref _languageId, value);
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

        private string? _text;

        public string? Text
        {
            get => _text;
            set => this.RaiseAndSetIfChanged(ref _text, value);
        }

        private int? _relatedTextUnitId;

        /// <summary>
        /// The id of the textunit the note belongs to or null in case of an unsorted note.
        /// </summary>
        [ForeignKey("RelatedTextUnit")]
        public int? RelatedTextUnitId
        {
            get => _relatedTextUnitId;
            set
            {
                this.RaiseAndSetIfChanged(ref _relatedTextUnitId, value);
                ModificationDate = DateTime.Now;
            }
        }

        private TextUnit? _relatedTextUnit;

        /// <summary>
        /// The textunit the note belongs to.
        /// </summary>
        public virtual TextUnit? RelatedTextUnit
        {
            get => _relatedTextUnit;
            set => this.RaiseAndSetIfChanged(ref _relatedTextUnit, value);
        }

        private bool _isReadonly;

        [NotMapped]
        public bool IsReadonly
        {
            get => _isReadonly;
            set => this.RaiseAndSetIfChanged(ref _isReadonly, value);
        }

        private ObservableCollection<Note>? _sourceNotes;

        public virtual ObservableCollection<Note>? SourceNotes
        {
            get => _sourceNotes;
            set => this.RaiseAndSetIfChanged(ref _sourceNotes, value);
        }

        private ObservableCollection<Note>? _derivedNotes;

        public virtual ObservableCollection<Note>? DerivedNotes
        {
            get => _derivedNotes;
            set => this.RaiseAndSetIfChanged(ref _derivedNotes, value);
        }

        private ObservableCollection<ContentSource>? _relatedSources;

        public virtual ObservableCollection<ContentSource>? RelatedSources
        {
            get => _relatedSources;
            set => this.RaiseAndSetIfChanged(ref _relatedSources, value);
        }

        #endregion
    }
}
