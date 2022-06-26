using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.DAL.Models;

namespace NoteEvolution.ViewModels
{
    public class TextUnitViewModel : ViewModelBase
    {
        #region Private Properties

        private readonly SourceCache<TextUnit, Guid> _textUnitChildListSource;

        private ReadOnlyObservableCollection<TextUnitViewModel> _textUnitChildListView;

        private Note _firstAddedNote = null;

        #endregion

        public TextUnitViewModel(TextUnit textUnit, DocumentViewModel parent)
        {
            Parent = parent;

            IsVisible = true;
            IsSelected = false;
            IsExpanded = true;

            Value = textUnit;

            FontSize = 12.0;
            // update current tree depth on changes of children
            this.WhenAnyValue(t => t.Value.SubtreeDepth, t => t.Value.HierarchyLevel, t => t.Parent.MaxFontSize)
                .Select(cv =>
                {
                    if (cv.Item1 == 0)
                        return 12.0;
                    return cv.Item3 - (cv.Item2 * 4.0);
                })
                .Where(nv => nv != FontSize)
                .Do(nv => FontSize = nv)
                .Subscribe();

            // update header on text changes
            Value.NoteListSource
                // Where(n => n.LanguageId == SelectedLanguageId)
                .Where(_ => _firstAddedNote == null)
                .OnItemAdded(n => {
                    _firstAddedNote = n;
                    n.WhenPropertyChanged(n => n.Text)
                        .Select(n => {
                            var text = (n.Value ?? "").Replace(Environment.NewLine, "");
                            return text.Substring(0, Math.Min(text.Length, 200));
                        })
                        .ToProperty(this, tvm => tvm.Header, out _header);
                    })
                .DisposeMany()
                .Subscribe();

            _textUnitChildListSource = textUnit.TextUnitListSource;

            if (_parent != null)
            {
                _parent
                    .WhenPropertyChanged(d => d.TextUnitListSource)
                    .Where(p => p.Value != null)
                    .Do(d =>
                    {
                        var noteComparer = SortExpressionComparer<TextUnitViewModel>.Ascending(tvm => tvm.Value.OrderNr);
                        var noteWasModified = _textUnitChildListSource
                            .Connect()
                            .WhenPropertyChanged(tu => tu.OrderNr)
                            .Select(_ => Unit.Default);
                        _parent.TextUnitListSource.Connect()
                            .Filter(n => n.Value.ParentId == Value.Id)
                            .Sort(noteComparer, noteWasModified)
                            .ObserveOn(RxApp.MainThreadScheduler)
                            .Bind(out _textUnitChildListView)
                            .DisposeMany()
                            .Subscribe();
                    })
                    .Subscribe();
            }
        }

        #region Public Properties

        private DocumentViewModel _parent;

        public DocumentViewModel Parent
        {
            get => _parent;
            set => this.RaiseAndSetIfChanged(ref _parent, value);
        }

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

        private double _fontSize;

        public double FontSize
        {
            get => _fontSize;
            set => this.RaiseAndSetIfChanged(ref _fontSize, value);
        }

        private Note _selectedNote;

        public Note SelectedNote
        {
            get => _selectedNote;
            set => this.RaiseAndSetIfChanged(ref _selectedNote, value);
        }

        private TextUnit _value;

        public TextUnit Value
        {
            get => _value;
            set => this.RaiseAndSetIfChanged(ref _value, value);
        }

        private ObservableAsPropertyHelper<string> _header;
        public string Header => _header?.Value ?? "";

        public ReadOnlyObservableCollection<TextUnitViewModel> TextUnitChildListView => _textUnitChildListView;

        #endregion
    }
}
