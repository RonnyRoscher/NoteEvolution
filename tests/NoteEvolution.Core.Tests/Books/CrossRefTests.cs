using System.Text;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Tests.Books;

public class CrossRefTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid C = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid Sub = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly Guid Unknown = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");

    private static string R(Guid id) => BlockRefs.Format(id);

    private static Book Load(string text) =>
        Book.Load(LogseqParser.Parse("Buch - Test.md", Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Find_PlainBoldItalicAndMalformed()
    {
        var text = $"a {R(A)} **b {R(B)}** *c {R(C)}* ((abc)) (({A}) ({B}";
        var refs = BlockRefs.Find(text);
        Assert.Equal([A, B, C], refs.Select(r => r.Id));
        Assert.All(refs, r => Assert.Equal(R(r.Id), text.Substring(r.Start, r.Length)));
        Assert.Empty(BlockRefs.Find("((abc)) ((1234)) (x)"));
    }

    [Fact]
    public void Find_UpperCaseUuid()
    {
        var refs = BlockRefs.Find($"x (({A.ToString().ToUpperInvariant()})) y");
        Assert.Equal(A, Assert.Single(refs).Id);
    }

    [Fact]
    public void Format_UsesDFormat() => Assert.Equal($"(({A:D}))", BlockRefs.Format(A));

    [Fact]
    public void Retarget_ReplacesMappedOnly()
    {
        var map = new Dictionary<Guid, Guid> { [A] = C };
        Assert.Equal($"vor {R(C)} mid {R(B)} nach", BlockRefs.Retarget($"vor {R(A)} mid {R(B)} nach", map));
        Assert.Equal($"x {R(C)}", BlockRefs.Retarget($"x (({A.ToString().ToUpperInvariant()}))", map));
    }

    [Fact]
    public void Remove_KeepsTextAndLeavesNoDoubleSpace()
    {
        var ids = new HashSet<Guid> { A, B };
        Assert.Equal("vor nach", BlockRefs.Remove($"vor {R(A)} nach", ids));
        Assert.Equal("vor", BlockRefs.Remove($"vor {R(A)}", ids));
        Assert.Equal("nach", BlockRefs.Remove($"{R(A)} nach", ids));
        Assert.Equal("a b", BlockRefs.Remove($"a {R(A)} {R(B)} b", ids));
        Assert.Equal($"a {R(C)} b", BlockRefs.Remove($"a {R(A)} {R(C)} b", ids));
        Assert.Equal("ab", BlockRefs.Remove($"a{R(A)}b", ids));
    }

    private static string Doc =>
        "type:: book\n\n" +
        $"- # Eins\n  id:: {A}\n" +
        $"\t- Text eins verweist auf {R(C)}\n" +
        $"\t  id:: {B}\n" +
        $"\t\t- Unterpunkt {R(A)}\n" +
        $"\t\t  id:: {Sub}\n" +
        $"\t\t\t- tief verweist auf {R(Unknown)} und {R(Sub)}\n" +
        $"- # Zwei\n" +
        $"\t- Text zwei {R(B)} und {R(A)}\n" +
        $"\t  id:: {C}\n";

    [Fact]
    public void Of_ResolvesHeadingAndTextBlockTargets()
    {
        var book = Load(Doc);
        var index = CrossRefIndex.Of(book);
        var heading = index.Target(A)!;
        Assert.Equal("Eins", heading.Heading!.Title);
        Assert.Null(heading.TextBlock);
        var block = index.Target(B)!;
        Assert.Null(block.Heading);
        Assert.Contains("Text eins", block.TextBlock!.Text);
        Assert.Null(index.Target(Unknown));
    }

    [Fact]
    public void To_IsInBookOrderAndCoversParagraphs()
    {
        var book = Load(Doc);
        var index = CrossRefIndex.Of(book);
        var toA = index.To(A);
        Assert.Equal(2, toA.Count);
        var first = book.Root.Children.First().TextBlocks.Single();
        var second = book.Root.Children.Last().TextBlocks.Single();
        Assert.Same(first, toA[0].TextBlock);
        Assert.Equal(first.Paragraphs[0].Block.Key, toA[0].ElementKey);
        Assert.Same(second, toA[1].TextBlock);
        Assert.Equal(second.Key, toA[1].ElementKey);
        Assert.Equal([second.Key], index.To(B).Select(s => s.ElementKey));
        Assert.Equal([first.Key], index.To(C).Select(s => s.ElementKey));
        Assert.Empty(index.To(Guid.NewGuid()));
    }

    [Fact]
    public void Of_ReferenceInASubPoint_IsIndexedOnceUnderTheParagraph()
    {
        var book = Load(
            "title:: T\ntype:: book\n\n" +
            "- # H\n" +
            $"\t- Ziel\n\t  id:: {A}\n" +
            $"\t- Eigener Text {R(A)}\n" +
            $"\t\t- Unterpunkt {R(A)} und {R(A)}\n");

        var sources = CrossRefIndex.Of(book).To(A);

        Assert.Equal(3, sources.Count);
        var textBlock = book.Root.Items.OfType<OutlineNode>().Single().Items.OfType<TextBlock>().Last();
        Assert.Equal(1, sources.Count(s => s.ElementKey == textBlock.Key));
        Assert.Equal(2, sources.Count(s => s.ElementKey == textBlock.Paragraphs.Single().Block.Key));
    }

    [Fact]
    public void Missing_ListsUnknownIdsAndSubPointTargets()
    {
        var book = Load(Doc);
        var index = CrossRefIndex.Of(book);
        Assert.Equal([Unknown, Sub], index.Missing.Select(s => s.TargetId));
        Assert.Null(index.Target(Sub));
        Assert.Empty(index.To(Sub));
    }
}
