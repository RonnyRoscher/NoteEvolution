using Microsoft.Extensions.Localization;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Repetitions;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Resources;

namespace NoteEvolution.UI.State;

/// <summary>What of a repetition is marked in the text, and the marks the editor shows (repetitions tab spec 3).</summary>
public static class RepetitionMarks
{
    /// <summary>Joins the places of a mark's tooltip.</summary>
    public const string PlaceSeparator = "; ";

    /// <summary>
    /// The stretches of <paramref name="side"/> (<see cref="Repetition.A"/> or <see cref="Repetition.B"/>) that make
    /// <paramref name="repetition"/> one, in <see cref="ComparedParagraph.Plain"/> offsets of the side's compared block:
    /// for a literal one each shared run, one piece per paragraph it touches; for a semantic one the sentences of that
    /// side. Empty if the block is not in <paramref name="set"/>.
    /// </summary>
    public static IReadOnlyList<(int Paragraph, int Start, int Length)> Spans(Repetition repetition, Guid side, RepetitionSet set)
    {
        if (set.Block(side) is not { } block)
        {
            return [];
        }

        var isA = side == repetition.A;
        if (repetition.Kind == RepetitionKind.Semantic)
        {
            return [.. repetition.Sentences
                .Select(pair => isA ? pair.A : pair.B)
                .Select(sentence => (sentence.Paragraph, sentence.Start, sentence.Length))
                .Distinct()];
        }

        var spans = new List<(int Paragraph, int Start, int Length)>();
        foreach (var run in repetition.Runs)
        {
            var first = isA ? run.StartA : run.StartB;
            var from = block.Words[first];
            for (var i = first + 1; i <= first + run.Words; i++)
            {
                // A piece ends before the run's end or before the first word of another paragraph.
                if (i < first + run.Words && block.Words[i].Paragraph == from.Paragraph)
                {
                    continue;
                }

                var to = block.Words[i - 1];
                spans.Add((from.Paragraph, from.Start, to.Start + to.Length - from.Start));
                if (i < first + run.Words)
                {
                    from = block.Words[i];
                }
            }
        }

        return spans;
    }

    /// <summary>
    /// The marks of <paramref name="set"/> in editor offsets: with <paramref name="underline"/> (the switch „Wiederholungen
    /// zeigen“) the literal repetitions that are not accepted, with the tab open (<paramref name="tabOpen"/>) the semantic
    /// ones too; with the tab open, whatever the switch, the origin side of <paramref name="focus"/> as
    /// <see cref="RepetitionMarkKind.Current"/> (an accepted pair only with <paramref name="showAccepted"/>, as the tab
    /// shows it then). The tooltip names the other places (<paramref name="placeOf"/>); a stretch marked the same
    /// way for several pairs is one mark that names them all. Empty if <paramref name="set"/> is not for <paramref name="book"/>.
    /// </summary>
    public static IReadOnlyList<RepetitionTextMark> For(
        RepetitionSet set, Book book, bool underline, bool tabOpen, RepetitionFocus? focus, bool showAccepted, Func<Guid, string> placeOf,
        IStringLocalizer<Strings> l)
    {
        if (!set.IsFor(book))
        {
            return [];
        }

        // Each stretch with the places it is repeated at; order keeps the stretches as they are first met.
        var places = new Dictionary<(Guid Key, int Start, int Length, RepetitionMarkKind Kind), List<string>>();
        var order = new List<(Guid Key, int Start, int Length, RepetitionMarkKind Kind)>();

        void Add(Repetition repetition, Guid side, Guid other, RepetitionMarkKind kind)
        {
            if (set.Block(side) is not { } block)
            {
                return;
            }

            var place = placeOf(other);
            foreach (var (paragraph, start, length) in Spans(repetition, side, set))
            {
                var (key, shownStart, shownLength) = ComparisonText.ShownSpan(block, paragraph, start, length);
                var mark = (key, shownStart, shownLength, kind);
                if (!places.TryGetValue(mark, out var list))
                {
                    places[mark] = list = [];
                    order.Add(mark);
                }

                if (!list.Contains(place))
                {
                    list.Add(place);
                }
            }
        }

        foreach (var repetition in set.All.Where(r => underline && !r.Accepted && (tabOpen || r.Kind == RepetitionKind.Literal)))
        {
            var kind = repetition.Kind == RepetitionKind.Literal ? RepetitionMarkKind.Literal : RepetitionMarkKind.Semantic;
            Add(repetition, repetition.A, repetition.B, kind);
            Add(repetition, repetition.B, repetition.A, kind);
        }

        if (tabOpen && focus is not null
            && set.All.FirstOrDefault(r => r.Kind == focus.Kind && r.A == focus.A && r.B == focus.B) is { } current
            && (showAccepted || !current.Accepted))
        {
            Add(current, focus.Origin, focus.Other, RepetitionMarkKind.Current);
        }

        return [.. order.Select(m => new RepetitionTextMark(
            m.Key, m.Start, m.Length, m.Kind, l["RepetitionAlsoIn", string.Join(PlaceSeparator, places[m])].Value))];
    }
}
