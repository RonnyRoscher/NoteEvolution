using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Tests.Books;

public class BlockDatesTests
{
    private static Page Parse(string text) =>
        LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text));

    private static Block BlockOf(string text) => Parse("- " + text + "\n").Roots.Single();

    // A fixed local time with the local offset, so the tests hold in any time zone.
    private static DateTimeOffset LocalNow(int hour = 14, int minute = 32, int second = 0)
    {
        var local = new DateTime(2026, 10, 19, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [Fact]
    public void Read_MinuteFormat() =>
        Assert.Equal(new DateTime(2026, 10, 19, 14, 32, 0),
            BlockDates.Created(BlockOf("Text\n  created:: 2026-10-19T14:32")));

    [Fact]
    public void Read_WithSeconds() =>
        Assert.Equal(new DateTime(2026, 10, 19, 14, 32, 45),
            BlockDates.Updated(BlockOf("Text\n  updated:: 2026-10-19T14:32:45")));

    [Fact]
    public void Read_WithZone_ConvertedToLocal()
    {
        var utc = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var expected = utc.ToLocalTime().DateTime;
        Assert.Equal(expected, BlockDates.Created(BlockOf("Text\n  created:: 2026-10-19T12:00:00Z")));
        Assert.Equal(expected, BlockDates.Updated(BlockOf("Text\n  updated:: 2026-10-19T14:00:00+02:00")));
    }

    [Theory]
    [InlineData("gestern")]
    [InlineData("2026-13-45T99:99")]
    [InlineData("")]
    public void Read_Garbage_Null_NoThrow(string value)
    {
        var block = BlockOf($"Text\n  created:: {value}\n  updated:: {value}");
        Assert.Null(BlockDates.Created(block));
        Assert.Null(BlockDates.Updated(block));
    }

    [Fact]
    public void Read_Missing_Null()
    {
        var block = BlockOf("Text");
        Assert.Null(BlockDates.Created(block));
        Assert.Null(BlockDates.Updated(block));
    }

    [Fact]
    public void StampNew_SetsBoth_Format()
    {
        var block = BlockOf("Text");
        BlockDates.StampNew(block, LocalNow());
        Assert.Equal("2026-10-19T14:32", block.GetProperty("created"));
        Assert.Equal("2026-10-19T14:32", block.GetProperty("updated"));
    }

    [Fact]
    public void StampChanged_KeepsCreated()
    {
        var block = BlockOf("Text\n  created:: 2026-10-01T08:00\n  updated:: 2026-10-01T08:00");
        BlockDates.StampChanged(block, LocalNow());
        Assert.Equal("2026-10-01T08:00", block.GetProperty("created"));
        Assert.Equal("2026-10-19T14:32", block.GetProperty("updated"));
    }

    [Fact]
    public void StampChanged_WithoutCreated_OnlyUpdated()
    {
        var block = BlockOf("Text");
        BlockDates.StampChanged(block, LocalNow());
        Assert.Null(block.GetProperty("created"));
        Assert.Equal("2026-10-19T14:32", block.GetProperty("updated"));
    }

    [Fact]
    public void StampChanged_OverwritesGarbageUpdated()
    {
        var block = BlockOf("Text\n  updated:: gestern");
        BlockDates.StampChanged(block, LocalNow());
        Assert.Equal("2026-10-19T14:32", block.GetProperty("updated"));
    }

    [Fact]
    public void TextBlock_ExposesDates()
    {
        var book = Book.Load(Parse(
            "type:: book\n\n" +
            "- # Kapitel\n" +
            "\t- Block\n" +
            "\t  created:: 2026-10-01T08:00\n" +
            "\t  updated:: 2026-10-19T14:32\n" +
            "\t- Ohne Datum\n"));
        var blocks = book.Root.Children.Single().TextBlocks.ToList();
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), blocks[0].Created);
        Assert.Equal(new DateTime(2026, 10, 19, 14, 32, 0), blocks[0].Updated);
        Assert.Null(blocks[1].Created);
        Assert.Null(blocks[1].Updated);
    }
}
