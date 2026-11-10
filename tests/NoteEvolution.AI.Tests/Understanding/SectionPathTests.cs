using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Understanding;

public class SectionPathTests
{
    [Fact]
    public void SectionPath_Of_AndFind_RoundTrip_WithSameTitles()
    {
        using var tv = TestVault.Create(("pages/Buch.md",
            "title:: Buch\ntype:: book\n\n- Vorwort\n- # Schlaf\n\t- ## Träume\n\t\t- Eins.\n\t- ## Träume\n\t\t- Zwei.\n- # Schlaf\n\t- ## Träume\n\t\t- Drei.\n"));
        var book = tv.Open().Books.Single();
        var first = book.Root.Children.First();
        var second = book.Root.Children.Last();

        Assert.Equal("", SectionPath.Of(book.Root));
        Assert.Equal("Schlaf#0", SectionPath.Of(first));
        Assert.Equal("Schlaf#0 › Träume#1", SectionPath.Of(first.Children.Last()));
        Assert.Equal("Schlaf#1 › Träume#0", SectionPath.Of(second.Children.Single()));
        foreach (var node in Nodes(book.Root))
        {
            Assert.Same(node, SectionPath.Find(book, SectionPath.Of(node)));
        }

        Assert.Null(SectionPath.Find(book, "Schlaf#2"));
        Assert.Null(SectionPath.Find(book, "Schlaf#0 › Arbeit#0"));
        Assert.Null(SectionPath.Find(book, "Schlaf"));
    }

    private static IEnumerable<OutlineNode> Nodes(OutlineNode node) => node.Children.SelectMany(Nodes).Prepend(node);
}
