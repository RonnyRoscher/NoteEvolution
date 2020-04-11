using System;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using NoteEvolution.Models;

namespace NoteEvolution.ViewModels
{
    public class NoteViewModel : ViewModelBase
    {
        public NoteViewModel(Note note)
        {
            Value = note;

            // update header on text changes
            Value.WhenPropertyChanged(n => n.Text)
                // FirstOrDefault(n => n.LanguageId == SelectedLanguageId)
                .Select(n => {
                        var text = (Value?.Text ?? "").Replace(Environment.NewLine, "");
                        return text.Substring(0, Math.Min(text.Length, 200));
                    })
                .ToProperty(this, n => n.Header, out _header);
        }

        #region Public Properties

        public Note Value { get; }

        readonly ObservableAsPropertyHelper<string> _header;
        public string Header => _header.Value;

        #endregion
    }
}
