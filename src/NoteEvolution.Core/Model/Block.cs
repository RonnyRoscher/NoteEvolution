using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Model;

/// <summary>
/// One Logseq bullet with its own raw lines (bullet line, properties, continuation lines and
/// following blank lines). <see cref="Bullet"/>, <see cref="Indent"/>, <see cref="Content"/>,
/// <see cref="Properties"/> and <see cref="Id"/> are derived from <see cref="Lines"/>.
/// </summary>
public sealed class Block
{
    private readonly List<RawLine> _lines;
    private readonly List<RawLine> _baseLines;
    private readonly List<Block> _children = [];
    private Derived? _derived;

    internal Block(IEnumerable<RawLine> lines, int sourceLineNumber)
    {
        _lines = [.. lines];
        if (_lines.Count == 0)
        {
            throw new ArgumentException("A block needs at least its bullet line.", nameof(lines));
        }

        _baseLines = [.. _lines];
        SourceLineNumber = sourceLineNumber;
    }

    /// <summary>Runtime key, unique per parsed block; never written to the file.</summary>
    public Guid Key { get; } = Guid.NewGuid();

    public char Bullet => D.Bullet;

    /// <summary>The indentation of the bullet line exactly as written (tabs and/or spaces).</summary>
    public string Indent => D.Indent;

    public Block? Parent { get; internal set; }

    public IReadOnlyList<Block> Children => _children;

    /// <summary>The block's own lines: bullet line, properties, continuation lines and following blank lines.</summary>
    public IReadOnlyList<RawLine> Lines => _lines;

    /// <summary>The lines as they were when the file was last loaded or saved.</summary>
    public IReadOnlyList<RawLine> BaseLines => _baseLines;

    /// <summary>Bullet text plus continuation lines (without properties), joined with <c>"\n"</c>.</summary>
    public string Content => D.Content;

    public IReadOnlyList<BlockProperty> Properties => D.Properties;

    /// <summary>The <c>id::</c> property if it parses as a Guid in format <c>D</c>, otherwise <c>null</c>.</summary>
    public Guid? Id => Guid.TryParseExact(GetProperty("id"), "D", out var id) ? id : null;

    public bool IsDirty { get; internal set; }

    /// <summary>Where the property lines are within <see cref="Lines"/>, and where a new one is inserted.</summary>
    internal PropertySection PropertySection => D.Section;

    /// <summary>1-based line number of the bullet line in the file when it was parsed.</summary>
    public int SourceLineNumber { get; internal set; }

    internal void AddChild(Block child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    /// <summary>Value of the first property named <paramref name="key"/> (case-insensitive), or <c>null</c>.</summary>
    public string? GetProperty(string key) => FindProperty(Properties, key);

    internal static string? FindProperty(IEnumerable<BlockProperty> properties, string key) =>
        properties.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Replaces the block's own lines and recomputes the derived values.</summary>
    internal void SetLines(IEnumerable<RawLine> lines)
    {
        var newLines = lines.ToList();
        if (newLines.Count == 0)
        {
            throw new ArgumentException("A block needs at least its bullet line.", nameof(lines));
        }

        _lines.Clear();
        _lines.AddRange(newLines);
        _derived = null;
    }

    private Derived D => _derived ??= Derived.From(_lines);

    private sealed record Derived(
        char Bullet, string Indent, string Content, IReadOnlyList<BlockProperty> Properties, PropertySection Section)
    {
        public static Derived From(IReadOnlyList<RawLine> lines)
        {
            if (!LogseqSyntax.TryParseBullet(lines[0].Text, out var indent, out var bullet, out var text))
            {
                (indent, bullet, text) = ("", '-', lines[0].Text);
            }

            var section = LogseqSyntax.FindPropertySection(lines, 0);
            var properties = new List<BlockProperty>();
            var content = new List<string> { text };
            for (var index = 1; index < lines.Count; index++)
            {
                if (index >= section.Start && index < section.InsertIndex)
                {
                    properties.Add(LogseqSyntax.TryParseProperty(lines[index].Text)!);
                }
                else
                {
                    content.Add(StripContinuation(lines[index].Text, indent));
                }
            }

            var count = content.Count;
            while (count > 1 && string.IsNullOrWhiteSpace(content[count - 1]))
            {
                count--;
            }

            return new Derived(bullet, indent, string.Join("\n", content.Take(count)), properties, section);
        }

        private static string StripContinuation(string line, string indent)
        {
            var rest = line.StartsWith(indent, StringComparison.Ordinal) ? line[indent.Length..] : line;
            var spaces = 0;
            while (spaces < 2 && spaces < rest.Length && rest[spaces] == ' ')
            {
                spaces++;
            }

            return rest[spaces..];
        }
    }
}
