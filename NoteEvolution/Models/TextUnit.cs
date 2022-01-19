using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace NoteEvolution.Models
{
    public class TextUnit : ReactiveObject
    {
        #region Private Properties

        private SourceCache<Note, Guid> _globalNoteListSource;
        public IObservable<IChangeSet<Note, Guid>> _noteListSource;
        private ReadOnlyObservableCollection<Note> _noteListView;

        public SourceCache<TextUnit, Guid> _globalTextUnitListSource;
        public IObservable<IChangeSet<TextUnit, Guid>> _textUnitListSource;

        #endregion

        public TextUnit()
        {
            Id = 0;
            LocalId = Guid.NewGuid();

            InitializeDataSources(new SourceCache<Note, Guid>(n => n.LocalId), new SourceCache<TextUnit, Guid>(t => t.LocalId));
        }

        public TextUnit(Document relatedDocument)
        {
            CreationDate = DateTime.Now;
            Id = 0;
            LocalId = Guid.NewGuid();
            CreationDate = DateTime.Now;
            ModificationDate = DateTime.Now;

            InitializeDataSources(relatedDocument.GlobalNoteListSource, relatedDocument.GlobalTextUnitListSource);

            RelatedDocument = relatedDocument;
            RelatedDocumentId = relatedDocument.Id;

            RelatedSources = new ObservableCollection<ContentSource>();
        }

        public void InitializeDataSources(SourceCache<Note, Guid> globalNoteListSource, SourceCache<TextUnit, Guid> globalTextUnitListSource)
        {
            // only set if not already set previously
            //if (_globalNoteListSource?.Items.Any() != true)
            {
                _globalNoteListSource = globalNoteListSource;
                _noteListSource = _globalNoteListSource
                    .Connect()
                    .Filter(n => n.RelatedTextUnitId == Id);

                _globalTextUnitListSource = globalTextUnitListSource;
                _textUnitListSource = _globalTextUnitListSource
                    .Connect()
                    .Filter(t => t.ParentId == Id);

                // load child textunits from db and keep updated when adding and deleting textunits, as well as update current tree depth on changes of children
                SubtreeDepth = 0;
                TextUnitChildList = new List<TextUnit>();
                _textUnitListSource
                    .OnItemAdded(t => { TextUnitChildList.Add(t); })
                    .OnItemRemoved(t => { TextUnitChildList.Remove(t); })
                    .WhenPropertyChanged(t => t.SubtreeDepth)
                    .Select(cv => TextUnitChildList.Max(t => t.SubtreeDepth) + 1)
                    .Where(nv => nv != SubtreeDepth)
                    .Do(nstd => SubtreeDepth = nstd)
                    .Subscribe();

                // load associated notes from db and keep updated when adding and deleting textunits
                NoteList = new ObservableCollection<Note>();
                _noteListSource
                    // todo: check is that the solution to double add / crashes
                    //.OnItemAdded(n => { 
                    //    if (!NoteList.Any(en => en.Id == n.Id))
                    //        NoteList.Add(n); 
                    //})
                    .OnItemRemoved(n => { NoteList.Remove(n); })
                    .DisposeMany()
                    .Subscribe();

                var noteComparer = SortExpressionComparer<Note>.Ascending(n => n.LanguageId);
                var rootTextUnitWasModified = _globalNoteListSource
                    .Connect()
                    .WhenPropertyChanged(n => n.LanguageId)
                    .Select(_ => Unit.Default);
                _globalNoteListSource
                    .Connect()
                    .Filter(n => n.RelatedTextUnitId == Id)
                    .Sort(noteComparer, rootTextUnitWasModified)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Bind(out _noteListView)
                    .DisposeMany()
                    .Subscribe();

                // update ModifiedDate on ModifiedDate changes
                this.WhenAnyValue(n => n.CreationDate, n => n.RelatedDocument, n => n.Parent, n => n.Predecessor, n => n.Successor)
                    .Throttle(TimeSpan.FromMilliseconds(250))
                    .Do(x => ModificationDate = DateTime.Now);
                _noteListSource
                    .WhenPropertyChanged(n => n.ModificationDate)
                    .Where(n => n.Sender.ModificationDate > ModificationDate)
                    .Skip(1)
                    .Throttle(TimeSpan.FromMilliseconds(250))
                    .Do(n => ModificationDate = DateTime.Now)
                    .Subscribe();
            }
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
                ParentId = Id,
                Parent = this
            };
            _globalTextUnitListSource.AddOrUpdate(newTextUnit);
            RelatedDocument.GlobalTextUnitListSource.AddOrUpdate(newTextUnit);
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
                ParentId = ParentId,
                Parent = Parent
            };
            RelatedDocument.GlobalTextUnitListSource.AddOrUpdate(newTextUnit);
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

        public void RemoveTextUnit()
        {
            var oldPredecessor = Predecessor;
            var oldSuccessor = Successor;
            _globalTextUnitListSource.Remove(this);
            if (oldPredecessor != null)
                oldPredecessor.Successor = oldSuccessor;
            if (oldSuccessor != null)
                oldSuccessor.Predecessor = oldPredecessor;
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

        private int _relatedDocumentId;

        /// <summary>
        /// The id of the document the textunit is associated with.
        /// </summary>
        [ForeignKey("RelatedDocument")]
        public int RelatedDocumentId
        {
            get => _relatedDocumentId;
            set => this.RaiseAndSetIfChanged(ref _relatedDocumentId, value);
        }

        private Document _relatedDocument;

        /// <summary>
        /// The document the textunit is associated with.
        /// </summary>
        public virtual Document RelatedDocument
        {
            get => _relatedDocument;
            set => this.RaiseAndSetIfChanged(ref _relatedDocument, value);
        }

        private int? _parentId;

        /// <summary>
        /// The id of the textunit's parent or null if it is the root.
        /// </summary>
        [ForeignKey("Parent")]
        public int? ParentId
        {
            get => _parentId;
            set => this.RaiseAndSetIfChanged(ref _parentId, value);
        }

        private TextUnit _parent;

        /// <summary>
        /// The textunits parent or null if it is the root.
        /// </summary>
        public virtual TextUnit Parent
        {
            get => _parent;
            set => this.RaiseAndSetIfChanged(ref _parent, value);
        }

        private TextUnit _predecessor;

        /// <summary>
        /// The textunits predecessor on the same hierarchy level if exists.
        /// </summary>
        [NotMapped]
        public virtual TextUnit Predecessor
        {
            get => _predecessor;
            set => this.RaiseAndSetIfChanged(ref _predecessor, value);
        }

        private int? _successorId;

        /// <summary>
        /// The id of the textunit's successor on the same hierarchy level if exists.
        /// </summary>
        [ForeignKey("Successor")]
        public int? SuccessorId
        {
            get => _successorId;
            set => this.RaiseAndSetIfChanged(ref _successorId, value);
        }

        private TextUnit _successor;

        /// <summary>
        /// The textunits successor on the same hierarchy level if exists.
        /// </summary>
        public virtual TextUnit Successor
        {
            get => _successor;
            set => this.RaiseAndSetIfChanged(ref _successor, value);
        }

        private List<TextUnit> _textUnitChildList;

        /// <summary>
        /// List of direct child textunits in the hierarchical tree.
        /// </summary>
        public virtual List<TextUnit> TextUnitChildList
        {
            get => _textUnitChildList;
            set => this.RaiseAndSetIfChanged(ref _textUnitChildList, value);
        }

        /// <summary>
        /// The hierarchy level of the textunit. Starting with 0 on the root level and increasing by 1 on each deeper child level.
        /// </summary>
        [NotMapped]
        public int HierarchyLevel => Parent?.HierarchyLevel + 1 ?? 0;

        private int _subtreeDepth;

        /// <summary>
        /// The depth of the textunits subtree. Starting with 0 on the leaf level and increasing by 1 each higher level towards the root.
        /// </summary>
        [NotMapped]
        public int SubtreeDepth
        {
            get => _subtreeDepth;
            set => this.RaiseAndSetIfChanged(ref _subtreeDepth, value);
        }

        private double _orderNr;

        /// <summary>
        /// A number between in predecessor and its successor used for sequencial ordering of textunits.
        /// </summary>
        [NotMapped]
        public double OrderNr
        {
            get => _orderNr;
            set => this.RaiseAndSetIfChanged(ref _orderNr, value);
        }

        private ObservableCollection<Note> _noteList;

        /// <summary>
        /// List of notes beloging to this textunit.
        /// </summary>
        public virtual ObservableCollection<Note> NoteList
        {
            get => _noteList;
            set => this.RaiseAndSetIfChanged(ref _noteList, value);
        }

        [NotMapped]
        public ReadOnlyObservableCollection<Note> NoteListView => _noteListView;

        private ObservableCollection<ContentSource> _relatedSources;

        public virtual ObservableCollection<ContentSource> RelatedSources
        {
            get => _relatedSources;
            set => this.RaiseAndSetIfChanged(ref _relatedSources, value);
        }

        [NotMapped]
        public IObservable<IChangeSet<Note, Guid>> NoteListSource => _noteListSource;

        [NotMapped]
        public SourceCache<TextUnit, Guid> TextUnitListSource => _globalTextUnitListSource;

        #endregion
    }
}
