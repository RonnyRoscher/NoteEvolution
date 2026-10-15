using System.Text.Json;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;

namespace NoteEvolution.AI.Tests.Quality;

/// <summary>
/// Smoke test of the quality reference (spec 9.2, <c>tests/quality</c>): the book and the notes parse, every note has an
/// expectation whose chapter and sub-chapter exist in the book, and the set has its required size. The measurement
/// program itself (<c>tests/quality/measure.cs</c>) needs Ollama and runs on demand only.
/// </summary>
public class ReferenceDataTests
{
    private const int NoteCount = 40;
    private const int MinOtherAspects = 15;

    private static string DataPath(string name) => Path.Combine(AppContext.BaseDirectory, "Quality", name);

    private static Book LoadBook()
    {
        var path = DataPath("Referenzbuch.md");
        var page = LogseqParser.Parse(path, File.ReadAllBytes(path));
        Assert.Null(page.ParseError);
        Assert.True(Book.IsBook(page));
        return Book.Load(page);
    }

    /// <summary>The first lines of the root bullets of <c>Notizen.md</c>.</summary>
    private static List<string> LoadNotes()
    {
        var path = DataPath("Notizen.md");
        var page = LogseqParser.Parse(path, File.ReadAllBytes(path));
        Assert.Null(page.ParseError);
        return [.. page.Roots.Select(block => block.Content.Split('\n', 2)[0].Trim())];
    }

    /// <summary><c>expected.json</c>: note first line → chapter title, sub-chapter title, "anderer Aspekt".</summary>
    private static Dictionary<string, Expectation> LoadExpected() =>
        JsonSerializer.Deserialize<Dictionary<string, Expectation>>(
            File.ReadAllBytes(DataPath("expected.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed record Expectation(string Chapter, string Subchapter, bool OtherAspect);

    [Fact]
    public void ExpectedPathsExist_EveryNoteCovered()
    {
        var book = LoadBook();
        var notes = LoadNotes();
        var expected = LoadExpected();

        Assert.Equal(notes.Count, notes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(notes.Order(StringComparer.Ordinal), expected.Keys.Order(StringComparer.Ordinal));
        foreach (var (note, expectation) in expected)
        {
            var chapter = book.Root.Children.SingleOrDefault(c => c.Title == expectation.Chapter);
            Assert.True(chapter is not null, $"Chapter '{expectation.Chapter}' of '{note}' is not in the book");
            Assert.True(chapter.Children.Count(c => c.Title == expectation.Subchapter) == 1,
                $"Sub-chapter '{expectation.Chapter} › {expectation.Subchapter}' of '{note}' is not in the book");
        }
    }

    [Fact]
    public void Reference_HasFortyNotes_AtLeastFifteenOtherAspects_SixToEightChapters()
    {
        var book = LoadBook();
        var expected = LoadExpected();

        Assert.Equal(NoteCount, LoadNotes().Count);
        Assert.True(expected.Values.Count(e => e.OtherAspect) >= MinOtherAspects);
        Assert.InRange(book.Root.Children.Count(), 6, 8);
        Assert.All(book.Root.Children, chapter =>
        {
            Assert.InRange(chapter.Children.Count(), 2, 3);
            Assert.All(chapter.Children, sub => Assert.True(sub.TextBlocks.Count() >= 2, $"'{sub.Title}' has too few text blocks"));
        });
        Assert.Empty(book.Warnings);
    }
}
