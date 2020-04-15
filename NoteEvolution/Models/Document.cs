﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using DynamicData;
using ReactiveUI;

namespace NoteEvolution.Models
{
    public class Document : ReactiveObject
    {
        public Document()
        {
            DocumentId = Guid.NewGuid();
            CreationDate = DateTime.Now;
            _textUnitListSource = new SourceCache<TextUnit, Guid>(n => n.TextUnitId);

            // update ModifiedDate on changes to local text unit properties
            this.WhenAnyValue(d => d.CreationDate, d => d.Title)
                .Select(_ => DateTime.Now)
                .ToProperty(this, d => d.ModificationDate, out _modificationDate);
            // update ModifiedDate on ModifiedDate changes in any associated text unit
            _textUnitListSource
                .Connect()
                .WhenPropertyChanged(tu => tu.ModificationDate)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .Select(tu => tu.Value)
                .ToProperty(this, d => d.ModificationDate, out _modificationDate);
        }

        #region Public Methods

        // todo: AddExisting & CreateNew
        public TextUnit AddTextUnit()
        {
            var latestRootTextUnit = TextUnitList.LastOrDefault();
            if (latestRootTextUnit != null)
                return latestRootTextUnit.AddSuccessor();
            return null;
        }

        public void RemoveTextUnit(TextUnit oldTextUnit)
        {
            if (oldTextUnit != null)
                _textUnitListSource.Remove(oldTextUnit);
        }

        #endregion

        #region Public Properties

        private Guid _documentId;

        public Guid DocumentId
        {
            get => _documentId;
            set => this.RaiseAndSetIfChanged(ref _documentId, value);
        }

        private DateTime _creationDate;

        public DateTime CreationDate
        {
            get => _creationDate;
            set => this.RaiseAndSetIfChanged(ref _creationDate, value);
        }

        readonly ObservableAsPropertyHelper<DateTime> _modificationDate;

        public DateTime ModificationDate => _modificationDate.Value;

        private string _title;

        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        public SourceCache<TextUnit, Guid> _textUnitListSource;

        public SourceCache<TextUnit, Guid> TextUnitListSource => _textUnitListSource;

        public IEnumerable<TextUnit> TextUnitList => _textUnitListSource.Items;

        #endregion
    }
}
