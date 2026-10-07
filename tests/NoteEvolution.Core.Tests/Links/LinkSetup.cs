using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;
using NoteEvolution.TestSupport;

namespace NoteEvolution.Core.Tests.Links;

/// <summary>A test vault with one book and one journal file, wired to a <see cref="LinkService"/>.</summary>
internal sealed class LinkSetup : IDisposable
{
    public const string BookFile = "pages/Buch - Test.md";
    public const string JournalFile = "journals/2026_03_01.md";

    public const string DefaultBook =
        "title:: Buch: Test\n" +
        "type:: book\n" +
        "\n" +
        "- Vorspann\n" +
        "- # Kapitel\n" +
        "\t- ## Abschnitt\n" +
        "\t\t- Erster Textblock\n" +
        "\t\t- Zweiter Textblock\n" +
        "\t\t- ### Unterabschnitt\n" +
        "\t\t\t- Text im Unterabschnitt\n";

    public const string DefaultJournal =
        "- Kern\n" +
        "  collapsed:: true\n" +
        "\t- Detail\n" +
        "- Zweite Notiz\n";

    private static readonly DateTimeOffset Now = new(2026, 3, 15, 14, 30, 5, TimeSpan.Zero);

    public LinkSetup(string book = DefaultBook, string journal = DefaultJournal)
    {
        Tv = TestVault.Create((BookFile, book), (JournalFile, journal));
        Reopen();
    }

    public TestVault Tv { get; }

    public Vault Vault { get; private set; } = null!;

    public FailingWriter Writer { get; private set; } = null!;

    public UndoManager Undo { get; private set; } = null!;

    public PendingStore Pending { get; private set; } = null!;

    public LinkService Links { get; private set; } = null!;

    public Book Book => Vault.FindBook("Buch - Test")!;

    public Page Journal => Vault.FindPageByPath(JournalPath)!;

    public string BookPath => Path.Combine(Tv.Root, BookFile);

    public string JournalPath => Path.Combine(Tv.Root, JournalFile);

    /// <summary>Simulates an app restart: everything is read again from the files.</summary>
    public void Reopen()
    {
        Vault = Tv.Open();
        var inner = new PageWriter(Vault, new BackupService(Vault.Root, new FakeClock(Now)), new SelfWriteRegistry())
        {
            Sleep = _ => { },
        };
        Writer = new FailingWriter(inner);
        Undo = new UndoManager();
        Pending = new PendingStore(Vault.Root);
        Links = new LinkService(Vault, Writer, Undo, Pending);
    }

    public Block Note(string content) => Journal.AllBlocks().Single(b => b.Content == content);

    public TextBlock Text(string text)
    {
        var book = Book;
        return book.Page.AllBlocks().Select(b => book.FindTextBlock(b.Key)).OfType<TextBlock>().Single(t => t.Text == text);
    }

    public Guid SectionKey(string title)
    {
        var book = Book;
        return book.Page.AllBlocks().Select(b => book.FindNode(b.Key)).OfType<OutlineNode>().Single(n => n.Title == title).Key;
    }

    public string ReadBook() => Tv.Read(BookFile);

    public string ReadJournal() => Tv.Read(JournalFile);

    public void Dispose() => Tv.Dispose();
}

/// <summary>Delegates to a real writer, but throws <see cref="IOException"/> for configured paths.</summary>
internal sealed class FailingWriter(IPageWriter inner) : IPageWriter
{
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> _skips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fails the next <paramref name="times"/> saves of the path, after <paramref name="skip"/> saves that succeed.</summary>
    public void FailNext(string path, int times = 1, int skip = 0)
    {
        _failures[Path.GetFullPath(path)] = times;
        _skips[Path.GetFullPath(path)] = skip;
    }

    public void Save(Page page)
    {
        var key = Path.GetFullPath(page.FilePath);
        if (_skips.TryGetValue(key, out var skip) && skip > 0)
        {
            _skips[key] = skip - 1;
        }
        else if (_failures.TryGetValue(key, out var remaining) && remaining > 0)
        {
            _failures[key] = remaining - 1;
            throw new IOException($"Simulierter Schreibfehler: {page.FilePath}");
        }

        inner.Save(page);
    }
}
