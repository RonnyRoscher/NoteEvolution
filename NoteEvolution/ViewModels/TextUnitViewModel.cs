using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;

namespace NoteEvolution.ViewModels
{
    public class TextUnitViewModel : ViewModelBase
    {
        #region Private Properties

        private readonly SourceCache<TextUnit, Guid> _textUnitChildListSource;

        private readonly ReadOnlyObservableCollection<TextUnitViewModel> _textUnitChildListView;

        private Note _firstAddedNote = null;

        private readonly DocumentViewModel _parent;

        #endregion

        public TextUnitViewModel(TextUnit textUnit, DocumentViewModel parent)
        {
            _parent = parent;

            IsVisible = true;
            IsSelected = false;
            IsExpanded = true;

            Value = textUnit;

            // update header on text changes
            Value.NoteListSource.Connect()
                // Where(n => n.LanguageId == SelectedLanguageId)
                .Where(_ => _firstAddedNote == null)
                .OnItemAdded(n => {
                    _firstAddedNote = n;
                    n.WhenPropertyChanged(n => n.Text)
                        .Select(n => {
                            var text = (n.Value ?? "").Replace(Environment.NewLine, "");
                            return text.Substring(0, Math.Min(text.Length, 200));
                        })
                        .ToProperty(this, tuvm => tuvm.Header, out _header);
                    })
                .DisposeMany()
                .Subscribe();

            _textUnitChildListSource = textUnit.TextUnitChildListSource;

            if (_parent?.TextUnitListSource != null)
            {
                var noteComparer = SortExpressionComparer<TextUnitViewModel>.Ascending(tuvm => tuvm.Value.OrderNr);
                var noteWasModified = _textUnitChildListSource
                    .Connect()
                    .WhenPropertyChanged(tu => tu.ModificationDate)
                    .Select(_ => Unit.Default);
                _parent.TextUnitListSource.Connect()
                    .Filter(n => n.Value.Parent?.TextUnitId == Value.TextUnitId)
                    .Sort(noteComparer, noteWasModified)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Bind(out _textUnitChildListView)
                    .DisposeMany()
                    .Subscribe();
            }
        }

        #region Public Properties

        private bool _isVisible;

        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        private bool _isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        private bool _isExpanded;

        public bool IsExpanded
        {
            get => _isExpanded;
            set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
        }

        private TextUnit _value;

        public TextUnit Value
        {
            get => _value;
            set => this.RaiseAndSetIfChanged(ref _value, value);
        }

        private ObservableAsPropertyHelper<string> _header;
        public string Header => _header.Value;

        public ReadOnlyObservableCollection<TextUnitViewModel> TextUnitChildListView => _textUnitChildListView;

        #endregion
    }
}
