using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ReactiveUI;

namespace NoteEvolution.DAL.Models
{
    public class Language : ReactiveObject
    {
        public Language()
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

        private string? _name;

        /// <summary>
        /// The name of the language.
        /// </summary>
        public string? Name
        {
            get => _name;
            set => this.RaiseAndSetIfChanged(ref _name, value);
        }

        private int _orderNr;

        /// <summary>
        /// The order number in which the language should be displayed, starting from 0.
        /// </summary>
        public int OrderNr
        {
            get => _orderNr;
            set => this.RaiseAndSetIfChanged(ref _orderNr, value);
        }

        #endregion

        #region Object Processing Functionality

        public Language Copy()
        {
            var copy = new Language()
            {
                Id = 0,
                Name = Name,
                OrderNr = OrderNr
            };
            return copy;
        }

        #endregion
    }
}
