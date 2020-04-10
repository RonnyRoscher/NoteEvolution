using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Linq;
using DynamicData;
using ReactiveUI;

namespace NoteEvolution.Models
{
    public class Note : ReactiveObject
    {
        public Note(Document relatedDocument)
        {
            NoteId = Guid.NewGuid();
            CreationDate = DateTime.Now;
            _childNotesSource = new SourceCache<Note, Guid>(n => n.NoteId);
            _contentSource = new SourceCache<Text, Guid>(t => t.TextId);
            _contentSource.AddOrUpdate(new Text());
            _contentSource.AddOrUpdate(new Text(2));

            // update ModifiedDate on changes to local note properties
            this.WhenAnyValue(n => n.CreationDate, n => n.RelatedDocument, n => n.Parent, n => n.Predecessor, n => n.Successor, n => n.Content)
                .Select(_ => DateTime.Now)
                .ToProperty(this, n => n.ModificationDate, out _modificationDate);

            // update header on text changes
            _contentSource
                .Connect()
                .WhenPropertyChanged(n => n.Value)
                // FirstOrDefault(n => n.LanguageId == SelectedLanguageId)
                .Select(n => (Content.FirstOrDefault()?.Value ?? "").Replace(Environment.NewLine, "").Substring(0, Math.Min((Content.FirstOrDefault()?.Value ?? "").Length, 200)))
                .ToProperty(this, n => n.Header, out _header);

            RelatedDocument = relatedDocument;
            RelatedDocument.GetNoteListSource().AddOrUpdate(this);
        }

        #region Private Methods

        /// <summary>
        /// Retrieve the last sequencial note of the subtree.
        /// </summary>
        /// <param name="successor">The root of the subtree.</param>
        /// <returns>The last sequencial note of the subtree or the subtree root note if it does not have any children.</returns>
        private Note GetLastSubtreeNote(Note subtreeRoot)
        {
            var result = subtreeRoot;
            if (subtreeRoot.ChildNotes.Count() > 0)
                result = GetLastSubtreeNote(subtreeRoot.ChildNotes.OrderBy(n => n.OrderNr).LastOrDefault());
            return result;
        }

        /// <summary>
        /// Retrieve the note that comes directly before the given note in the sequencial structure.
        /// </summary>
        /// <param name="successor">The note which's predecessor is requested.</param>
        /// <returns>The sequencial predecessor note. If the given note is null or the first note of the document null is returned.</returns>
        private Note GetSequencialPredecessor(Note successor)
        {
            var directPrecesessor = successor?.Predecessor;
            if (directPrecesessor != null)
            {
                if (directPrecesessor.ChildNotes.Count() > 0)
                    return GetLastSubtreeNote(directPrecesessor.ChildNotes.OrderBy(n => n.OrderNr).LastOrDefault());
                return directPrecesessor;
            }
            return successor?.Parent;
        }

        /// <summary>
        /// Retrieve the note that comes directly after the given note in the sequencial structure.
        /// </summary>
        /// <param name="predecessor">The note which's successor is requested.</param>
        /// <returns>The sequencial successor note. If the given note is null or the last sequencial note of the document null is returned.</returns>
        private Note GetSequencialSuccessor(Note predecessor)
        {
            if (predecessor == null)
                return null;
            if (predecessor.Successor != null)
                return predecessor.Successor;
            return GetSequencialSuccessor(predecessor?.Parent);
        }

        #endregion

        #region Public Methods

        public SourceCache<Note, Guid> GetChildNoteListSource()
        {
            return _childNotesSource;
        }

        /// <summary>
        /// Create a new note and add it as child to this note. The child is always added as the first child.
        /// </summary>
        /// <returns>The created note if the creation was successful, or else null.</returns>
        public Note AddChild()
        {
            var previousFirstChild = ChildNotes.OrderBy(n => n.OrderNr).FirstOrDefault();
            // create note with document relations
            var newNote = new Note(RelatedDocument);
            // add hierarchical relations
            newNote.Parent = this;
            _childNotesSource.AddOrUpdate(newNote);
            // add sequencial relations
            if (previousFirstChild != null)
            {
                previousFirstChild.Predecessor = newNote;
                newNote.Successor = previousFirstChild;
            }
            // determine and set order number
            if (newNote.Successor != null)
            {
                // case: insert between parent and its previous first child
                newNote.OrderNr = (newNote.Parent.OrderNr + newNote.Successor.OrderNr) / 2.0;
            } else {
                var sequencialSuccessor = GetSequencialSuccessor(newNote.Parent);
                if (sequencialSuccessor != null)
                {
                    // case: insert as first child between parent and the next sequencial successor
                    newNote.OrderNr = (newNote.Parent.OrderNr + sequencialSuccessor.OrderNr) / 2.0;
                } else {
                    // case: insert as first child at the end of the document
                    newNote.OrderNr = newNote.Parent.OrderNr + 1.0;
                }
            }
            return newNote;
        }

        /// <summary>
        /// Create a new note and add it as successor to the given parent note. If the given note already has a successor the new note is inserte inbetween.
        /// </summary>
        /// <returns>The created note if the creation was successful, or else null.</returns>
        public Note AddSuccessor()
        {
            // create note with document relations
            var newNote = new Note(RelatedDocument);
            // add hierarchical relations
            newNote.Parent = Parent;
            if (Parent != null)
                newNote.Parent.GetChildNoteListSource().AddOrUpdate(newNote);
            // add sequencial relations
            var previousSuccessor = Successor;
            newNote.Predecessor = this;
            Successor = newNote;
            // handle insert inbetween if predecessor had predecessor
            if (previousSuccessor != null)
            {
                newNote.Successor = previousSuccessor;
                previousSuccessor.Predecessor = newNote;
            }
            // determine and set order number
            if (Parent == null)
            {
                // case: insert as latest note on the document root level
                newNote.OrderNr = RelatedDocument.NoteList.Max(n => n.OrderNr) + 1.0;
            } else {
                var sequencialSuccessor = GetSequencialSuccessor(newNote);
                if (sequencialSuccessor != null)
                {
                    // case: insert in between the new note's sequencial predecessor and successor
                    var sequencialPredecessor = GetSequencialPredecessor(newNote);
                    newNote.OrderNr = (sequencialPredecessor.OrderNr + sequencialSuccessor.OrderNr) / 2.0;
                } else {
                    // case: insert as last note at the end of the document
                    newNote.OrderNr = RelatedDocument.NoteList.Max(n => n.OrderNr) + 1.0;
                }
            }
            if (newNote.Parent == null)
                RelatedDocument.GetRootNoteListSource().AddOrUpdate(newNote);
            return newNote;
        }

        public void RemoveNote(Note oldNote)
        {
            if (oldNote != null)
                _childNotesSource.Remove(oldNote);
        }

        #endregion

        #region Public Properties

        private Guid _noteId;

        public Guid NoteId
        {
            get => _noteId;
            set => this.RaiseAndSetIfChanged(ref _noteId, value);
        }

        private DateTime _creationDate;

        public DateTime CreationDate
        {
            get => _creationDate;
            set => this.RaiseAndSetIfChanged(ref _creationDate, value);
        }

        readonly ObservableAsPropertyHelper<DateTime> _modificationDate;
        public DateTime ModificationDate => _modificationDate.Value;

        private Document _relatedDocument;

        public Document RelatedDocument
        {
            get => _relatedDocument;
            set => this.RaiseAndSetIfChanged(ref _relatedDocument, value);
        }

        public int HierachyLevel => Parent?.HierachyLevel + 1 ?? 0;

        private int[] _treeMaxDepth;

        public int[] TreeMaxDepth
        {
            get => _treeMaxDepth;
            set => this.RaiseAndSetIfChanged(ref _treeMaxDepth, value);
        }

        private double _orderNr;

        public double OrderNr
        {
            get => _orderNr;
            set => this.RaiseAndSetIfChanged(ref _orderNr, value);
        }

        private Note _parent;

        public Note Parent
        {
            get => _parent;
            set => this.RaiseAndSetIfChanged(ref _parent, value);
        }

        private Note _predecessor;

        public Note Predecessor
        {
            get => _predecessor;
            set => this.RaiseAndSetIfChanged(ref _predecessor, value);
        }

        private Note _successor;

        public Note Successor
        {
            get => _successor;
            set => this.RaiseAndSetIfChanged(ref _successor, value);
        }

        public SourceCache<Note, Guid> _childNotesSource;

        public IEnumerable<Note> ChildNotes => _childNotesSource.Items;


        public SourceCache<Text, Guid> _contentSource;

        public IEnumerable<Text> Content => _contentSource.Items;

        readonly ObservableAsPropertyHelper<string> _header;
        public string Header => _header.Value;

        #endregion
    }
}
