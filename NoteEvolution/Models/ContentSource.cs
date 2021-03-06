using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ReactiveUI;

namespace NoteEvolution.Models
{
    public class ContentSource : ReactiveObject
    {
        public ContentSource()
        {
            Id = 0;
            LocalId = Guid.NewGuid();
            CreationDate = DateTime.Now;
            ModificationDate = DateTime.Now;
        }

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

        private string _author;

        /// <summary>
        /// The author of the source.
        /// </summary>
        public string Author
        {
            get => _author;
            set => this.RaiseAndSetIfChanged(ref _author, value);
        }

        private string _title;

        /// <summary>
        /// The main title of the document source
        /// </summary>
        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        private string _chapter;

        /// <summary>
        /// The chapter title of the document source.
        /// </summary>
        public string Chapter
        {
            get => _chapter;
            set => this.RaiseAndSetIfChanged(ref _chapter, value);
        }

        private int? _pageNumber;

        /// <summary>
        /// The page number the quote starts from, in case of a document source.
        /// </summary>
        public int? PageNumber
        {
            get => _pageNumber;
            set => this.RaiseAndSetIfChanged(ref _pageNumber, value);
        }

        private string _url;

        /// <summary>
        /// The url of the website or video in case the source is from the internet.
        /// </summary>
        public string Url
        {
            get => _url;
            set => this.RaiseAndSetIfChanged(ref _url, value);
        }

        private DateTime? _timestamp;

        /// <summary>
        /// The timestamp the quote starts from, in case of audio or video source.
        /// </summary>
        public DateTime? Timestamp
        {
            get => _timestamp;
            set => this.RaiseAndSetIfChanged(ref _timestamp, value);
        }

        private int? _relatedNoteId;

        /// <summary>
        /// The id of the note the source belongs to.
        /// </summary>
        [ForeignKey("RelatedNote")]
        public int? RelatedNoteId
        {
            get => _relatedNoteId;
            set
            {
                this.RaiseAndSetIfChanged(ref _relatedNoteId, value);
                ModificationDate = DateTime.Now;
            }
        }

        private Note _relatedNote;

        /// <summary>
        /// The note the source belongs to.
        /// </summary>
        public virtual Note RelatedNote
        {
            get => _relatedNote;
            set => this.RaiseAndSetIfChanged(ref _relatedNote, value);
        }

        private int? _relatedTextUnitId;

        /// <summary>
        /// The id of the textunit the source belongs to.
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

        private TextUnit _relatedTextUnit;

        /// <summary>
        /// The textunit the source belongs to.
        /// </summary>
        public virtual TextUnit RelatedTextUnit
        {
            get => _relatedTextUnit;
            set => this.RaiseAndSetIfChanged(ref _relatedTextUnit, value);
        }

        #endregion

        #region Object Processing Functionality

        public ContentSource Copy(bool withRelatedProperties = true)
        {
            var copy = new ContentSource()
            {
                Id = 0,
                Author = Author,
                Title = Title,
                Chapter = Chapter,
                PageNumber = PageNumber,
                Url = Url,
                Timestamp = Timestamp,
            };
            if (withRelatedProperties)
            {
                copy.RelatedNote = RelatedNote;
                copy.RelatedTextUnit = RelatedTextUnit;
            }
            return copy;
        }

        #endregion
    }
}
