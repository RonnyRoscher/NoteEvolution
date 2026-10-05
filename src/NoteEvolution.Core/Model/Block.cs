using NoteEvolution.Core.Format;

namespace NoteEvolution.Core.Model;

/// <summary>
/// One Logseq bullet with its own raw lines (bullet line, properties, continuation lines and
/// following blank lines). <see cref="Bullet"/>, <see cref="Indent"/>, <see cref="Content"/>,
/// <see cref="Properties"/> and <see cref="Id"/> are derived from <see cref="Lines"/>.
/// Edits change only the lines they have to; all other lines stay byte-identical.
/// </summary>
public sealed class Block
{
    private readonly List<RawLine> _lines;
    private readonly List<RawLine> _baseLines;
    private readonly List<Block> _children = [];
    private Derived? _derived;

    internal Block(IEnumerable<RawLine> lines, int sourceLineNumber)
        : this(lines, sourceLineNumber, detached: false)
    {
    }

    private Block(IEnumerable<RawLine> lines, int sourceLineNumber, bool detached)
    {
        _lines = [.. lines];
        if (_lines.Count == 0)
        {
            throw new ArgumentException("A block needs at least its bullet line.", nameof(lines));
        }

        _baseLines = detached ? [] : [.. _lines];
        SourceLineNumber = sourceLineNumber;
    }

    /// <summary>Runtime key, unique per block instance; never written to the file.</summary>
    public Guid Key { get; } = Guid.NewGuid();

    public char Bullet => D.Bullet;

    /// <summary>The indentation of the bullet line exactly as written (tabs and/or spaces).</summary>
    public string Indent => D.Indent;

    public Block? Parent { get; internal set; }

    public IReadOnlyList<Block> Children => _children;

    /// <summary>The block's own lines: bullet line, properties, continuation lines and following blank lines.</summary>
    public IReadOnlyList<RawLine> Lines => _lines;

    /// <summary>The lines as they were when the file was last loaded or saved; empty for a block that was never saved.</summary>
    public IReadOnlyList<RawLine> BaseLines => _baseLines;

    /// <summary>Bullet text plus continuation lines (without properties), joined with <c>"\n"</c>.</summary>
    public string Content => D.Content;

    public IReadOnlyList<BlockProperty> Properties => D.Properties;

    /// <summary>The <c>id::</c> property if it parses as a Guid in format <c>D</c>, otherwise <c>null</c>.</summary>
    public Guid? Id => Guid.TryParseExact(GetProperty("id"), "D", out var id) ? id : null;

    /// <summary>The block was changed (or inserted, moved) since the page was last loaded or saved.</summary>
    public bool IsDirty { get; internal set; }

    /// <summary>Where the property lines are within <see cref="Lines"/>, and where a new one is inserted.</summary>
    internal PropertySection PropertySection => D.Section;

    /// <summary>1-based line number of the bullet line in the file when it was parsed; 0 for a created block.</summary>
    public int SourceLineNumber { get; internal set; }

    /// <summary>The page the block belongs to; <c>null</c> while detached.</summary>
    internal Page? Page { get; private set; }

    private string NewLine => Page?.NewLine ?? "\n";

    /// <summary>
    /// A new block that belongs to no page, with bullet <c>-</c> and no indentation;
    /// <see cref="Page.InsertBlock"/> renders it in the style of its target.
    /// </summary>
    public static Block CreateDetached(string content, IEnumerable<BlockProperty>? properties = null)
    {
        var block = new Block([new RawLine("-", "\n")], 0, detached: true);
        block.SetContent(content);
        foreach (var property in properties ?? [])
        {
            block.SetProperty(property.Key, property.Value);
        }

        block.IsDirty = false;
        return block;
    }

    /// <summary>
    /// Copies the block and its whole subtree as detached blocks (new keys, empty <see cref="BaseLines"/>).
    /// Trailing blank lines are not copied; with <paramref name="withoutProperties"/> neither are property lines.
    /// </summary>
    public Block CloneDetached(bool withoutProperties)
    {
        var lines = _lines.ToList();
        if (withoutProperties)
        {
            lines.RemoveRange(PropertySection.Start, PropertySection.Count);
        }

        var count = lines.Count;
        while (count > 1 && string.IsNullOrWhiteSpace(lines[count - 1].Text))
        {
            count--;
        }

        var clone = new Block(lines.Take(count), 0, detached: true);
        foreach (var child in _children)
        {
            clone.InsertChild(clone._children.Count, child.CloneDetached(withoutProperties));
        }

        return clone;
    }

    /// <summary>Value of the first property named <paramref name="key"/> (case-insensitive), or <c>null</c>.</summary>
    public string? GetProperty(string key) => FindProperty(Properties, key);

    internal static string? FindProperty(IEnumerable<BlockProperty> properties, string key) =>
        properties.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>
    /// Replaces the first property line named <paramref name="key"/> (case-insensitive) in place, or inserts
    /// <c>Indent + "  " + key + ":: " + value</c> after the last property line (directly after the bullet
    /// line, or after the code fence the bullet line opens, if there are none).
    /// </summary>
    /// <exception cref="ArgumentException">Invalid key, or a value spanning several lines.</exception>
    /// <exception cref="InvalidOperationException">The bullet line opens a code fence that is never closed.</exception>
    public void SetProperty(string key, string value)
    {
        var text = LogseqSyntax.FormatProperty(Indent + "  ", key, value);
        var section = PropertySection;
        if (section.FenceUnclosed)
        {
            throw new InvalidOperationException("The block's code fence is not closed; it has no place for properties.");
        }

        for (var index = section.Start; index < section.InsertIndex; index++)
        {
            var property = LogseqSyntax.TryParseProperty(_lines[index].Text)!;
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                if (!LogseqSyntax.IsSame(property, key, value))
                {
                    _lines[index] = _lines[index] with { Text = text };
                    Changed();
                }

                return;
            }
        }

        InsertLine(section.InsertIndex, text);
        Changed();
    }

    /// <summary>Removes every property line named <paramref name="key"/> (case-insensitive).</summary>
    public void RemoveProperty(string key)
    {
        var section = PropertySection;
        var removed = false;
        for (var index = section.InsertIndex - 1; index >= section.Start; index--)
        {
            if (string.Equals(LogseqSyntax.TryParseProperty(_lines[index].Text)!.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                _lines.RemoveAt(index);
                removed = true;
            }
        }

        if (removed)
        {
            Changed();
        }
    }

    /// <summary>
    /// Sets the text (bullet text plus continuation lines, separated by <c>"\n"</c>). Lines are compared one by
    /// one with the current <see cref="Content"/>; only differing lines are rewritten, surplus ones removed and
    /// extra ones inserted after the last content line (never inside the property section). Property lines and
    /// trailing blank lines stay untouched.
    /// </summary>
    public void SetContent(string content)
    {
        var target = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        while (target.Count > 1 && string.IsNullOrWhiteSpace(target[^1]))
        {
            target.RemoveAt(target.Count - 1);
        }

        var current = Content.Split('\n');
        var section = PropertySection;
        var contentIndices = Enumerable.Range(0, _lines.Count)
            .Where(i => i < section.Start || i >= section.InsertIndex)
            .Take(current.Length)
            .ToList();
        var changed = false;

        for (var j = 0; j < Math.Min(current.Length, target.Count); j++)
        {
            if (current[j] != target[j])
            {
                var index = contentIndices[j];
                _lines[index] = _lines[index] with { Text = ContentLineText(j, target[j]) };
                changed = true;
            }
        }

        for (var j = current.Length - 1; j >= target.Count; j--)
        {
            _lines.RemoveAt(contentIndices[j]);
            changed = true;
        }

        var at = Math.Max(contentIndices[^1] + 1, section.InsertIndex);
        for (var j = current.Length; j < target.Count; j++)
        {
            InsertLine(at++, ContentLineText(j, target[j]));
            changed = true;
        }

        if (changed)
        {
            Changed();
        }
    }

    /// <summary>Returns the block's <see cref="Id"/>, adding an <c>id::</c> property with a new lowercase UUID if it has none.</summary>
    public Guid EnsureId()
    {
        if (Id is { } existing)
        {
            return existing;
        }

        var id = Guid.CreateVersion7();
        SetProperty("id", id.ToString("D"));
        return id;
    }

    internal void AddChild(Block child) => InsertChild(_children.Count, child);

    internal void InsertChild(int index, Block child)
    {
        child.Parent = this;
        child.SetPage(Page);
        _children.Insert(index, child);
    }

    internal void RemoveChild(Block child) => _children.Remove(child);

    /// <summary>Removes and returns all children (their <see cref="Parent"/> is left for the caller to reset).</summary>
    internal List<Block> TakeChildren()
    {
        var children = _children.ToList();
        _children.Clear();
        return children;
    }

    internal void SetPage(Page? page)
    {
        Page = page;
        foreach (var child in _children)
        {
            child.SetPage(page);
        }
    }

    /// <summary>
    /// Renders the lines for a new position: the bullet line gets <paramref name="indent"/> and
    /// <paramref name="bullet"/>, every other line has its old <see cref="Indent"/> prefix replaced by
    /// <paramref name="indent"/>, and every line ends with <paramref name="newLine"/>.
    /// </summary>
    internal void Render(string indent, char bullet, string newLine)
    {
        var oldIndent = Indent;
        for (var index = 0; index < _lines.Count; index++)
        {
            var text = index == 0
                ? indent + bullet + _lines[0].Text[(oldIndent.Length + 1)..]
                : ReplacePrefix(_lines[index].Text, oldIndent, indent);
            _lines[index] = new RawLine(text, newLine);
        }

        Changed();
    }

    /// <summary>Replaces the prefix <paramref name="oldPrefix"/> by <paramref name="newPrefix"/> in every line that has it.</summary>
    internal void Reindent(string oldPrefix, string newPrefix)
    {
        for (var index = 0; index < _lines.Count; index++)
        {
            _lines[index] = _lines[index] with { Text = ReplacePrefix(_lines[index].Text, oldPrefix, newPrefix) };
        }

        Changed();
    }

    internal void SetEnding(int index, string ending)
    {
        _lines[index] = _lines[index] with { Ending = ending };
        IsDirty = true;
    }

    internal void MarkSaved()
    {
        _baseLines.Clear();
        _baseLines.AddRange(_lines);
        IsDirty = false;
    }

    /// <summary>Text of content line <paramref name="j"/> (0 = bullet line) for <paramref name="text"/>.</summary>
    private string ContentLineText(int j, string text) =>
        j == 0 ? Indent + Bullet + (text.Length > 0 ? " " + text : "")
        : text.Length > 0 ? Indent + "  " + text
        : "";

    /// <summary>Inserts a new line; a line ending is added to the previous line if it had none (end of file).</summary>
    private void InsertLine(int index, string text)
    {
        if (index > 0 && _lines[index - 1].Ending.Length == 0)
        {
            _lines[index - 1] = _lines[index - 1] with { Ending = NewLine };
        }

        _lines.Insert(index, new RawLine(text, NewLine));
    }

    private void Changed()
    {
        _derived = null;
        IsDirty = true;
    }

    /// <summary>Blank or whitespace-only lines are never re-indented.</summary>
    private static string ReplacePrefix(string line, string oldPrefix, string newPrefix) =>
        !string.IsNullOrWhiteSpace(line) && line.StartsWith(oldPrefix, StringComparison.Ordinal)
            ? newPrefix + line[oldPrefix.Length..]
            : line;

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
