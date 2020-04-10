using System;
using System.Reactive.Linq;
using System.Linq;
using ReactiveUI;
using System.Collections.Generic;

namespace NoteEvolution.Models
{
    /// <summary>
    /// Class describing a simple unstructured text object.
    /// </summary>
    public class Text : ReactiveObject
    {
        private bool _modificationDateUnlocked;

        public Text(byte languageId = 1)
        {
            _modificationDateUnlocked = true;

            TextId = Guid.NewGuid();
            CreationDate = DateTime.Now;
            LanguageId = languageId;

            // update ModifiedDate on changes to local note properties
            this.WhenAnyValue(t => t.CreationDate, t => t.Value, t => t._modificationDateUnlocked)
                .Where(t => t.Item3)
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

        private Guid _textId;

        public Guid TextId
        {
            get => _textId;
            set => this.RaiseAndSetIfChanged(ref _textId, value);
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
        /// Usage of this text in documents and its text elements.
        /// </summary>
        public Dictionary<Guid, HashSet<Guid>> Usage
        {
            get => _usage;
            set => this.RaiseAndSetIfChanged(ref _usage, value);
        }

        #endregion
    }
}
