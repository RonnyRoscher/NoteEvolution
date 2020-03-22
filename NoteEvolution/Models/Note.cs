using System;
using ReactiveUI;

namespace NoteEvolution.Models
{
    public class Note : ReactiveObject
    {
        public Note()
        {
            Id = Guid.NewGuid();
            CreationDate = DateTime.Now;
            ModificationDate = CreationDate;
        }

        private Guid _id;

        public Guid Id
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

        private string _content;

        public string Content
        {
            get => _content;
            set
            {
                if (value != _content)
                    ModificationDate = DateTime.Now;
                this.RaiseAndSetIfChanged(ref _content, value);
            }
        }
    }
}
