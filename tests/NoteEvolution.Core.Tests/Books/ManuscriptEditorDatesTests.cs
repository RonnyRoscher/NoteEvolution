using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Tests.Books;

/// <summary>The manuscript commands stamp <c>created::</c> / <c>updated::</c> on the text blocks they create or change.</summary>
public class ManuscriptEditorDatesTests
{
    private const string Old = "2026-01-02T03:04";
    private const string Stamp = "2026-10-19T14:32";

    private const string Text =
        "type:: book\n" +
        "\n" +
        "- # Eins\n" +
        "\t- Text eins\n" +
        $"\t  created:: {Old}\n" +
        $"\t  updated:: {Old}\n" +
        "\t\t- Detail 1\n" +
        "\t- Text zwei\n";

    private const string Touched = $"\t  created:: {Old}\n\t  updated:: {Stamp}\n";

    // A fixed local time with the local offset, so the tests hold in any time zone.
    private static readonly DateTimeOffset Now = LocalNow();

    private static DateTimeOffset LocalNow()
    {
        var local = new DateTime(2026, 10, 19, 14, 32, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static Book Load() => Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(Text)));

    private static string Serialize(Book book) => Encoding.UTF8.GetString(PageSerializer.Serialize(book.Page));

    private static BookElement ElementOf(Book book, string content) =>
        BookElements.Find(book, book.Page.AllBlocks().Single(b => b.Content == content).Key)!;

    /// <summary>The remark is a detail: it gets no dates, its text block is changed.</summary>
    [Fact]
    public void AddRemark_RemarkGetsNoDates_TextBlockUpdated()
    {
        var book = Load();

        ManuscriptEditor.AddRemark(book, ElementOf(book, "Text eins"), Now);

        Assert.Equal(
            Text.Replace($"\t  created:: {Old}\n\t  updated:: {Old}\n", Touched)
                .Replace("\t\t- Detail 1\n", "\t\t- Detail 1\n\t\t-\n\t\t  status:: bemerkung\n"),
            Serialize(book));
    }

    [Fact]
    public void SetStatus_Updates()
    {
        var book = Load();

        ManuscriptEditor.SetStatus(book, ElementOf(book, "Text eins"), BlockStatus.Done, Now);

        Assert.Equal(
            Text.Replace($"\t  updated:: {Old}\n", $"\t  updated:: {Stamp}\n\t  status:: fertig\n"),
            Serialize(book));
    }

    [Fact]
    public void SetStatus_OnDetail_UpdatesItsTextBlock()
    {
        var book = Load();

        ManuscriptEditor.SetStatus(book, ElementOf(book, "Detail 1"), BlockStatus.Remark, Now);

        Assert.Equal(
            Text.Replace($"\t  updated:: {Old}\n", $"\t  updated:: {Stamp}\n")
                .Replace("\t\t- Detail 1\n", "\t\t- Detail 1\n\t\t  status:: bemerkung\n"),
            Serialize(book));
    }

    [Fact]
    public void SetStatus_Unchanged_NoDates()
    {
        var book = Load();

        ManuscriptEditor.SetStatus(book, ElementOf(book, "Text eins"), BlockStatus.Draft, Now);

        Assert.Equal(Text, Serialize(book));
    }

    [Fact]
    public void SetStatus_WithoutNow_StampsNothing()
    {
        var book = Load();

        ManuscriptEditor.SetStatus(book, ElementOf(book, "Text zwei"), BlockStatus.Done);

        Assert.Equal(Text.Replace("\t- Text zwei\n", "\t- Text zwei\n\t  status:: fertig\n"), Serialize(book));
    }

    /// <summary>Review focus 3: undo goes through the page snapshot the way <c>ManuscriptCommands</c> pushes its undo action.</summary>
    [Fact]
    public void SetStatus_Undo_RestoresDates()
    {
        var book = Load();
        var before = PageStructureSnapshot.Capture(book.Page);

        ManuscriptEditor.SetStatus(book, ElementOf(book, "Text eins"), BlockStatus.Done, Now);
        var after = PageStructureSnapshot.Capture(book.Page);
        Assert.NotEqual(Text, Serialize(book));
        Assert.True(after.Matches(book.Page));

        before.RestoreInto(book.Page);

        Assert.Equal(Text, Serialize(book));
    }

    [Fact]
    public void InsertAfter_TextBlock_HasBothDates()
    {
        var book = Load();

        ManuscriptEditor.InsertAfter(book, ElementOf(book, "Text eins"), Now);

        var after = Serialize(book);
        Assert.Contains($"\t-\n\t  created:: {Stamp}\n\t  updated:: {Stamp}\n", after);
        Assert.Contains($"\t  created:: {Old}\n\t  updated:: {Old}\n", after);
    }

    [Fact]
    public void InsertAfter_Heading_NoDates()
    {
        var book = Load();

        ManuscriptEditor.InsertAfter(book, ElementOf(book, "# Eins"), Now);

        Assert.DoesNotContain(Stamp, Serialize(book));
    }

    [Fact]
    public void InsertChild_OfTextBlock_DetailGetsNoDatesTextBlockUpdated()
    {
        var book = Load();

        ManuscriptEditor.InsertChild(book, ElementOf(book, "Text eins"), Now);

        var after = Serialize(book);
        Assert.Equal(1, after.Split(Stamp).Length - 1);
        Assert.Contains(Touched, after);
    }

    [Fact]
    public void DeleteDetail_UpdatesItsTextBlock()
    {
        var book = Load();

        ManuscriptEditor.DeleteDetail(book, ElementOf(book, "Detail 1").Key, Now);

        Assert.Equal(
            Text.Replace($"\t  updated:: {Old}\n", $"\t  updated:: {Stamp}\n").Replace("\t\t- Detail 1\n", ""),
            Serialize(book));
    }

    [Fact]
    public void Wrap_StampsNothing()
    {
        var book = Load();

        ManuscriptEditor.Wrap(book, ElementOf(book, "Text eins"));

        Assert.DoesNotContain(Stamp, Serialize(book));
    }
}
