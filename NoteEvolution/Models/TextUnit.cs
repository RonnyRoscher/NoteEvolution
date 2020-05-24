using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Linq;
using DynamicData;
using ReactiveUI;

namespace NoteEvolution.Models
{
    public class TextUnit : ReactiveObject
    {
        public TextUnit(Document relatedDocument)
        {
            RelatedDocument = relatedDocument;
            TextUnitId = Guid.NewGuid();
            CreationDate = DateTime.Now;
            // todo: set CurrentTreeDepth to 0 on TextUnit create
            _textUnitChildListSource = new SourceCache<TextUnit, Guid>(n => n.TextUnitId);
            _noteListSource = new SourceCache<Note, Guid>(t => t.NoteId);
            _noteListSource.AddOrUpdate(new Note());

            // update ModifiedDate on changes to local note properties
            this.WhenAnyValue(n => n.CreationDate, n => n.RelatedDocument, n => n.Parent, n => n.Predecessor, n => n.Successor, n => n.NoteList)
                .Select(_ => DateTime.Now)
                .ToProperty(this, n => n.ModificationDate, out _modificationDate);

            SubtreeDepth = 0;
            // update current tree depth on changes of children
            _textUnitChildListSource
                .Connect()
                .WhenPropertyChanged(tu => tu.SubtreeDepth)
                .Select(cv => TextUnitChildList.Max(tu => tu.SubtreeDepth) + 1)
                .Where(nv => nv != SubtreeDepth)
                .Do(nstd => SubtreeDepth = nstd)
                .Subscribe();
        }

        #region Private Methods

        /// <summary>
        /// Retrieve the last sequencial text unit of the subtree.
        /// </summary>
        /// <param name="successor">The root of the subtree.</param>
        /// <returns>The last sequencial text unit of the subtree or the subtree root text unit if it does not have any children.</returns>
        private TextUnit GetLastSubtreeTextUnit(TextUnit subtreeRoot)
        {
            var result = subtreeRoot;
            if (subtreeRoot.TextUnitChildList.Count() > 0)
                result = GetLastSubtreeTextUnit(subtreeRoot.TextUnitChildList.OrderBy(n => n.OrderNr).LastOrDefault());
            return result;
        }

        /// <summary>
        /// Retrieve the text unit that comes directly before the given text unit in the sequencial structure.
        /// </summary>
        /// <param name="successor">The text unit which's predecessor is requested.</param>
        /// <returns>The sequencial predecessor text unit. If the given text unit is null or the first text unit of the document null is returned.</returns>
        private TextUnit GetSequencialPredecessor(TextUnit successor)
        {
            var directPrecesessor = successor?.Predecessor;
            if (directPrecesessor != null)
            {
                if (directPrecesessor.TextUnitChildList.Count() > 0)
                    return GetLastSubtreeTextUnit(directPrecesessor.TextUnitChildList.OrderBy(n => n.OrderNr).LastOrDefault());
                return directPrecesessor;
            }
            return successor?.Parent;
        }

        /// <summary>
        /// Retrieve the text unit that comes directly after the given text unit in the sequencial structure.
        /// </summary>
        /// <param name="predecessor">The text unit which's successor is requested.</param>
        /// <returns>The sequencial successor text unit. If the given text unit is null or the last sequencial text unit of the document null is returned.</returns>
        private TextUnit GetSequencialSuccessor(TextUnit predecessor)
        {
            if (predecessor == null)
                return null;
            if (predecessor.Successor != null)
                return predecessor.Successor;
            return GetSequencialSuccessor(predecessor?.Parent);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Create a new text unit and add it as child to this text unit. The child is always added as the first child.
        /// </summary>
        /// <returns>The created text unit if the creation was successful, or else null.</returns>
        public TextUnit AddChild()
        {
            var previousFirstChild = TextUnitChildList.OrderBy(n => n.OrderNr).FirstOrDefault();
            // create text unit with document relations
            var newTextUnit = new TextUnit(RelatedDocument)
            {
                // add hierarchical relations
                Parent = this
            };
            _textUnitChildListSource.AddOrUpdate(newTextUnit);
            RelatedDocument.TextUnitListSource.AddOrUpdate(newTextUnit);
            // add sequencial relations
            if (previousFirstChild != null)
            {
                previousFirstChild.Predecessor = newTextUnit;
                newTextUnit.Successor = previousFirstChild;
            }
            // determine and set order number
            if (newTextUnit.Successor != null)
            {
                // case: insert between parent and its previous first child
                newTextUnit.OrderNr = (newTextUnit.Parent.OrderNr + newTextUnit.Successor.OrderNr) / 2.0;
            } else {
                var sequencialSuccessor = GetSequencialSuccessor(newTextUnit.Parent);
                if (sequencialSuccessor != null)
                {
                    // case: insert as first child between parent and the next sequencial successor
                    newTextUnit.OrderNr = (newTextUnit.Parent.OrderNr + sequencialSuccessor.OrderNr) / 2.0;
                } else {
                    // case: insert as first child at the end of the document
                    newTextUnit.OrderNr = newTextUnit.Parent.OrderNr + 1.0;
                }
            }
            return newTextUnit;
        }

        /// <summary>
        /// Create a new text unit and add it as successor to the given parent text unit. If the given text unit already has a successor the new text unit is inserte inbetween.
        /// </summary>
        /// <returns>The created text unit if the creation was successful, or else null.</returns>
        public TextUnit AddSuccessor()
        {
            // create text unit with document relations
            var newTextUnit = new TextUnit(RelatedDocument)
            {
                // add hierarchical relations
                Parent = Parent
            };
            RelatedDocument.TextUnitListSource.AddOrUpdate(newTextUnit);
            if (Parent != null)
                newTextUnit.Parent.TextUnitChildListSource.AddOrUpdate(newTextUnit);
            // add sequencial relations
            var previousSuccessor = Successor;
            newTextUnit.Predecessor = this;
            Successor = newTextUnit;
            // handle insert inbetween if predecessor had predecessor
            if (previousSuccessor != null)
            {
                newTextUnit.Successor = previousSuccessor;
                previousSuccessor.Predecessor = newTextUnit;
            }
            // determine and set order number
            if (Parent == null && previousSuccessor == null)
            {
                // case: insert as latest text unit on the document root level
                newTextUnit.OrderNr = RelatedDocument.TextUnitList.Max(n => n.OrderNr) + 1.0;
            } else {
                var sequencialSuccessor = GetSequencialSuccessor(newTextUnit);
                if (sequencialSuccessor != null)
                {
                    // case: insert in between the new text unit's sequencial predecessor and successor
                    var sequencialPredecessor = GetSequencialPredecessor(newTextUnit);
                    newTextUnit.OrderNr = (sequencialPredecessor.OrderNr + sequencialSuccessor.OrderNr) / 2.0;
                } else {
                    // case: insert as last text unit at the end of the document
                    newTextUnit.OrderNr = RelatedDocument.TextUnitList.Max(n => n.OrderNr) + 1.0;
                }
            }
            return newTextUnit;
        }

        public void RemoveTextUnit(TextUnit oldTextUnit)
        {
            if (oldTextUnit != null)
                _textUnitChildListSource.Remove(oldTextUnit);
        }

        #endregion

        #region Public Properties

        private Guid _textUnitId;

        public Guid TextUnitId
        {
            get => _textUnitId;
            set => this.RaiseAndSetIfChanged(ref _textUnitId, value);
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

        /// <summary>
        /// The document the textunit is associated with.
        /// </summary>
        public Document RelatedDocument
        {
            get => _relatedDocument;
            set => this.RaiseAndSetIfChanged(ref _relatedDocument, value);
        }

        private TextUnit _parent;

        /// <summary>
        /// The textunits parent or null if it is the root.
        /// </summary>
        public TextUnit Parent
        {
            get => _parent;
            set => this.RaiseAndSetIfChanged(ref _parent, value);
        }

        private TextUnit _predecessor;

        /// <summary>
        /// The textunits predecessor on the same hierarchy level if exists.
        /// </summary>
        public TextUnit Predecessor
        {
            get => _predecessor;
            set => this.RaiseAndSetIfChanged(ref _predecessor, value);
        }

        private TextUnit _successor;

        /// <summary>
        /// The textunits successor on the same hierarchy level if exists.
        /// </summary>
        public TextUnit Successor
        {
            get => _successor;
            set => this.RaiseAndSetIfChanged(ref _successor, value);
        }

        public SourceCache<TextUnit, Guid> _textUnitChildListSource;

        public SourceCache<TextUnit, Guid> TextUnitChildListSource => _textUnitChildListSource;

        public IEnumerable<TextUnit> TextUnitChildList => _textUnitChildListSource.Items;

        /// <summary>
        /// The hierarchy level of the textunit. Starting with 0 on the root level and increasing by 1 on each deeper child level.
        /// </summary>
        public int HierarchyLevel => Parent?.HierarchyLevel + 1 ?? 0;

        private int _subtreeDepth;

        /// <summary>
        /// The depth of the textunits subtree. Starting with 0 on the leaf level and increasing by 1 each higher level towards the root.
        /// </summary>
        public int SubtreeDepth
        {
            get => _subtreeDepth;
            set => this.RaiseAndSetIfChanged(ref _subtreeDepth, value);
        }

        private double _orderNr;

        /// <summary>
        /// A number between in predecessor and its successor used for sequencial ordering of textunits.
        /// </summary>
        public double OrderNr
        {
            get => _orderNr;
            set => this.RaiseAndSetIfChanged(ref _orderNr, value);
        }

        public SourceCache<Note, Guid> _noteListSource;

        public SourceCache<Note, Guid> NoteListSource => _noteListSource;

        public IEnumerable<Note> NoteList => _noteListSource.Items;

        #endregion
    }
}
