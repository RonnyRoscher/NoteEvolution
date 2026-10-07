using System.Text;
using NoteEvolution.Core.Format;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Format;

public class TextDocumentTests
{
    [Fact]
    public void Parse_KeepsBomEndingsAndMissingFinalNewline()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a\r\nb\r\nc\nd")];

        var doc = TextDocument.Parse(bytes);

        Assert.True(doc.HasBom);
        Assert.Equal(["a", "b", "c", "d"], doc.Lines.Select(l => l.Text));
        Assert.Equal(["\r\n", "\r\n", "\n", ""], doc.Lines.Select(l => l.Ending));
        Assert.Equal("\r\n", doc.DominantEnding);
        Assert.Equal(bytes, TextDocument.Encode(doc.HasBom, doc.Lines));
    }

    [Fact]
    public void Parse_EmptyFile_HasNoLines_AndDominantEndingLf()
    {
        var doc = TextDocument.Parse([]);

        Assert.False(doc.HasBom);
        Assert.Empty(doc.Lines);
        Assert.Equal("\n", doc.DominantEnding);
        Assert.Empty(TextDocument.Encode(doc.HasBom, doc.Lines));
    }

    [Fact]
    public void Parse_InvalidUtf8_ThrowsWithLineNumber()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("ok\n"), 0xC3, 0x28];

        var ex = Assert.Throws<ParseException>(() => TextDocument.Parse(bytes));

        Assert.Equal(2, ex.LineNumber);
    }

    [Fact]
    public void AtomicWrite_ReplacesExistingFile_AndLeavesNoTempFile()
    {
        using var dir = new TempDir();
        var path = dir.Write("note.md", "old");

        AtomicFile.Write(path, Encoding.UTF8.GetBytes("new"));

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFileSystemEntries(dir.Path));
    }
}
