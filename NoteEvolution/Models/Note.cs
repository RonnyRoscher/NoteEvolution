using System;
using System.Reactive.Linq;
using System.Linq;
using ReactiveUI;
using System.Collections.Generic;

namespace NoteEvolution.Models
{
    /// <summary>
    /// Class describing a simple unstructured note object.
    /// </summary>
    public class Note : ReactiveObject
    {
        private bool _modificationDateUnlocked;

        public Note(byte languageId = 1)
        {
            _modificationDateUnlocked = true;

            NoteId = Guid.NewGuid();
            CreationDate = DateTime.Now;
            LanguageId = languageId;

            // update ModifiedDate on changes to local note properties
            this.WhenAnyValue(n => n.CreationDate, n => n.Value, n => n._modificationDateUnlocked)
                .Where(n => n.Item3)
                .Select(_ => DateTime.Now)
                .ToProperty(this, n => n.ModificationDate, out _modificationDate);
        }

        #region Public Methods

        public void SetValueWithoutModificationDateChange(string newValue)
        {
            _modificationDateUnlocked = false;
            Value = newValue;
            _modificationDateUnlocked = true;
        }

        #endregion

        #region Public Properties

        private Guid _noteId;

        public Guid NoteId
        {
            get => _noteId;
            set => this.RaiseAndSetIfChanged(ref _noteId, value);
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

        readonly ObservableAsPropertyHelper<DateTime> _modificationDate;
        public DateTime ModificationDate => _modificationDate.Value;

        private string _value;

        public string Value
        {
            get => _value;
            set => this.RaiseAndSetIfChanged(ref _value, value);
        }

        private Dictionary<Guid, HashSet<Guid>> _usage;

        /// <summary>
        /// Usage of this note in documents and its text units.
        /// </summary>
        public Dictionary<Guid, HashSet<Guid>> Usage
        {
            get => _usage;
            set => this.RaiseAndSetIfChanged(ref _usage, value);
        }

        #endregion
    }
}
