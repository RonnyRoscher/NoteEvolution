using System;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Enums;
using NoteEvolution.Models;

namespace NoteEvolution.ViewModels
{
    public class NoteListViewModelBase : ViewModelBase
    {
        private bool _hideUsedNotes;

        public bool HideUsedNotes
        {
            get => _hideUsedNotes;
            set => this.RaiseAndSetIfChanged(ref _hideUsedNotes, value);
        }

        private NoteSortOrderType _sortOrder;

        public NoteSortOrderType SortOrder
        {
            get => _sortOrder;
            set => this.RaiseAndSetIfChanged(ref _sortOrder, value);
        }

        protected SortExpressionComparer<NoteViewModel> BuildNotesComparer(NoteSortOrderType arg)
        {
            switch (SortOrder)
            {
                case NoteSortOrderType.CreatedDesc:
                    return SortExpressionComparer<NoteViewModel>.Descending(nvm => nvm.Value.CreationDate);
                case NoteSortOrderType.CreatedAsc:
                    return SortExpressionComparer<NoteViewModel>.Ascending(nvm => nvm.Value.CreationDate);
                case NoteSortOrderType.ModifiedDesc:
                    return SortExpressionComparer<NoteViewModel>.Descending(nvm => nvm.Value.ModificationDate);
                case NoteSortOrderType.ModifiedAsc:
                    return SortExpressionComparer<NoteViewModel>.Ascending(nvm => nvm.Value.ModificationDate);
            }
            return null;
        }

        protected Func<Note, bool> BuildNotesFilter(object param)
        {
            return n =>
            {
                if (n.RelatedTextUnitId != null)
                    return false;
                return HideUsedNotes
                    ? !n.IsReadonly
                    : true;
            };
        }
    }
}
