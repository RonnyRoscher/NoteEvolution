﻿using System;
using System.Reactive.Linq;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using ReactiveUI;

namespace NoteEvolution.Models
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
            Usage = new Dictionary<int, HashSet<int>>();

            // update ModifiedDate on changes to local note properties
            this.WhenAnyValue(n => n.Text, n => n._modificationDateUnlocked)
                .Skip(1)
                .Where(n => n.Item2)
                .Throttle(TimeSpan.FromSeconds(0.5), RxApp.MainThreadScheduler)
                .Do(n => ModificationDate = DateTime.Now)
                .Subscribe();
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

        private string _text;

        public string Text
        {
            get => _text;
            set => this.RaiseAndSetIfChanged(ref _text, value);
        }

        private int? _relatedTextUnitId;

        /// <summary>
        /// The id of the textunit the note belongs to or null in case of an unsorted note.
        /// </summary>
        [ForeignKey("TextUnit")]
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
        /// The textunit the note belongs to.
        /// </summary>
        public virtual TextUnit RelatedTextUnit
        {
            get => _relatedTextUnit;
            set => this.RaiseAndSetIfChanged(ref _relatedTextUnit, value);
        }

        private int? _derivedNoteId;

        /// <summary>
        /// The id of the note that is directly derived from this one or null if this note is not a source note.
        /// </summary>
        [ForeignKey("DerivedNote")]
        public int? DerivedNoteId
        {
            get => _derivedNoteId;
            set
            {
                this.RaiseAndSetIfChanged(ref _derivedNoteId, value);
                IsReadonly = _derivedNoteId != null;
            }
        }

        private Note _derivedNote;

        /// <summary>
        /// The note that is directly derived from this one.
        /// </summary>
        public virtual Note DerivedNote
        {
            get => _derivedNote;
            set => this.RaiseAndSetIfChanged(ref _derivedNote, value);
        }

        private bool _isReadonly;

        [NotMapped]
        public bool IsReadonly
        {
            get => _isReadonly;
            set => this.RaiseAndSetIfChanged(ref _isReadonly, value);
        }

        private Dictionary<int, HashSet<int>> _usage;

        /// <summary>
        /// Usage of this note in documents and its text units.
        /// </summary>
        [NotMapped]
        public Dictionary<int, HashSet<int>> Usage
        {
            get => _usage;
            set => this.RaiseAndSetIfChanged(ref _usage, value);
        }

        #endregion
    }
}
