# NoteEvolution Stufe 1 – Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Lauffähiges Desktop-Schreibprogramm, das einen Logseq-Vault verlustfrei liest und schreibt, Notizen per „Übernehmen“ verknüpft ins Buch kopiert, Notizen per Volltext durchsucht und ein einfaches PDF erzeugt.

**Architecture:** Fünf Projekte nach Spec Abschnitt 2: `Core` (Format, Modell, Verknüpfungen, Speicherung), `AI` (in Stufe 1 nur die FTS5-Volltextsuche), `Pdf` (QuestPDF), `UI` (Razor-Komponenten + TipTap) und `Desktop` (Photino-Hülle). Jeder Block behält seine Originalzeilen; Änderungen sind zeilengenaue Eingriffe, sodass unveränderte Zeilen byteidentisch bleiben. Der Editor tauscht nur Dokument-JSON mit C# aus; der Abgleich mit dem Buchbaum (`BookSync`) liegt in Core.

**Tech Stack:** .NET 10, C#, xUnit, bUnit, Microsoft.Data.Sqlite (FTS5), QuestPDF, UglyToad.PdfPig (nur Tests), Photino.Blazor, TipTap 2 (lokal mit esbuild gebündelt), Serilog (File-Sink).

**Spec:** `docs/superpowers/specs/2026-10-05-noteevolution-design.md`

## Global Constraints

- Plattform .NET 10 (LTS); `global.json` mit `"version": "10.0.100", "rollForward": "latestFeature"`. Auf dem Entwicklungsrechner fehlt das SDK: `winget install Microsoft.DotNet.SDK.10`.
- Alle Projekte: `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props`.
- `NoteEvolution.Core`, `.AI` und `.Pdf` referenzieren keine UI-Pakete. Die UI spricht sie nur über Schnittstellen an.
- **Die Dateien im Vault sind die einzige Wahrheit.** Alles unter `.noteevolution/` ist wiederherstellbar oder ein Zusatz.
- Notizdateien ändern sich nur durch die Eigenschaftszeilen `id::` und `used-in::`, einzige Ausnahme ist der bestätigte `[handled]`-Assistent.
- Unveränderte Zeilen bleiben Byte für Byte erhalten (Einrückungsstil, Zeilenenden, Leerzeilen, BOM). Kodierung UTF-8.
- Schreiben ist atomar: zuerst eine temporäre Datei im selben Ordner, dann ersetzen. Eine Datei, die nicht sicher geparst werden kann, wird nie geschrieben.
- IDs sind UUIDs in Kleinbuchstaben im Format `D` (`Guid.CreateVersion7().ToString("D")`).
- Seitenlinks auf ein Buch verwenden den **Dateinamen ohne `.md`** (Beispiel `[[Buch - LoveMagic]]`), weil Obsidian Links über den Dateinamen auflöst.
- Oberflächentexte sind deutsch und stehen ausschließlich in `src/NoteEvolution.UI/Resources/Strings.resx`. Razor-Dateien enthalten keine deutschen Literale.
- TipTap ist die einzige JavaScript-Komponente. Sie ist lokal gebündelt (kein CDN). JS enthält nur das Editorschema, Editierbefehle und die Weiterleitung von Ereignissen; jede Fachlogik liegt in C#.
- Keine persönlichen Texte im Repository. Testdaten werden künstlich erzeugt.
- Automatisches Speichern 1000 ms nach der letzten Eingabe. Sicherungen werden 30 Tage aufbewahrt.

## Review Focus

1. **Codezäune in Notizen:** Zeilen wie `- foo` innerhalb von ```` ``` ```` sind Inhalt und keine Blöcke. Ein nicht geschlossener Zaun macht die Datei schreibgeschützt (Test in Task 2).
2. **CRLF, BOM und fehlender Zeilenumbruch am Dateiende:** Der Round-Trip ist byteidentisch, und neu eingefügte Zeilen übernehmen CRLF (Tests in Task 1 und Task 3).
3. **Suchtext mit FTS5-Sonderzeichen** (`"`, `*`, `-`, `(`, `AND`, `NEAR`): kein Absturz, Treffer nach den enthaltenen Wörtern (Test in Task 15).
4. **Eine Notiz an zwei Buchstellen verwendet:** `used-in::` hat zwei Einträge. Das Entfernen einer Quelle lässt den anderen Eintrag stehen (Test in Task 8).
5. **Logseq ändert die Buchdatei, während der Editor offen ist:** Nach dem Neuladen bleiben die Laufzeitschlüssel erhalten, und das nächste automatische Speichern erzeugt keine doppelten Blöcke (Test in Task 12).

---

## Dateistruktur

```
NoteEvolution.slnx, global.json, Directory.Build.props, .gitignore
src/NoteEvolution.Core/
  Format/      TextDocument.cs, RawLine.cs, AtomicFile.cs, ParseException.cs, LogseqParser.cs, PageSerializer.cs
  Model/       Page.cs, Block.cs, BlockProperty.cs, IndentStyle.cs
  Text/        InlineMarkdown.cs, NoteTag.cs
  Links/       UsedInValue.cs, SourceValue.cs, ILinkService.cs, LinkService.cs, InsertPosition.cs,
               PendingStore.cs, LinkChecker.cs, UndoManager.cs
  Books/       Book.cs, OutlineNode.cs, TextBlock.cs, Paragraph.cs, BookSnapshot.cs, BookSync.cs, OutlineEditor.cs
  Vaults/      IVault.cs, Vault.cs, VaultSettings.cs, NoteBlock.cs, INoteRepository.cs, NoteRepository.cs
  Storage/     IClock.cs, IPageWriter.cs, PageWriter.cs, BackupService.cs, SelfWriteRegistry.cs,
               VaultWatcher.cs, ConflictDetector.cs, PageMerger.cs, RuntimeKeys.cs, ExternalChangeHandler.cs
  Assistants/  HandledConverter.cs, DraftConverter.cs
src/NoteEvolution.AI/Search/    ISearchService.cs, FullTextSearchService.cs, FtsQuery.cs
src/NoteEvolution.Pdf/          IPdfExporter.cs, QuestPdfExporter.cs, ExportText.cs, Fonts/*.ttf, Fonts/OFL.txt
src/NoteEvolution.UI/           Razor-Klassenbibliothek (siehe Tasks 17–21)
src/NoteEvolution.Desktop/      Program.cs, PhotinoPlatformServices.cs
tests/NoteEvolution.{Core,AI,Pdf,UI}.Tests/
```

---

### Task 1: Lösungsgerüst und Textdatei-Ebene

**Files:**
- Create: `global.json`, `Directory.Build.props`, `.gitignore` (bin, obj, node_modules, `.noteevolution/`), `NoteEvolution.slnx`
- Create: alle Projekte aus der Dateistruktur (`dotnet new classlib` für Core, AI und Pdf; `dotnet new razorclasslib` für UI; `dotnet new console` für Desktop; `dotnet new xunit` für die vier Testprojekte; Projekt-Referenzen gemäß Spec-Tabelle Abschnitt 2)
- Create: `src/NoteEvolution.Core/Format/RawLine.cs`, `TextDocument.cs`, `AtomicFile.cs`, `ParseException.cs`
- Test: `tests/NoteEvolution.Core.Tests/Format/TextDocumentTests.cs`, `tests/NoteEvolution.Core.Tests/TempDir.cs`

**Interfaces:**
- Produces: `record RawLine(string Text, string Ending)` (`Ending` ∈ `"\n"`, `"\r\n"`, `""`); `sealed class TextDocument { bool HasBom; IReadOnlyList<RawLine> Lines; string DominantEnding; static TextDocument Parse(byte[] bytes); static byte[] Encode(bool hasBom, IEnumerable<RawLine> lines); }`; `static class AtomicFile { static void Write(string path, byte[] bytes); }`; `class ParseException(string message, int lineNumber) : Exception` mit `int LineNumber`; Test-Helfer `sealed class TempDir : IDisposable { string Path; string Write(string relPath, string content); }`.

- [ ] **Step 1: .NET-10-SDK installieren und Gerüst anlegen.** Ausführen: `dotnet --list-sdks`. Erwartet: eine Zeile `10.0.x`.
- [ ] **Step 2: Failing tests schreiben**

```csharp
[Fact] public void Parse_KeepsBomEndingsAndMissingFinalNewline() {
    byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a\r\nb\r\nc\nd")];
    var doc = TextDocument.Parse(bytes);
    Assert.True(doc.HasBom);
    Assert.Equal(["a", "b", "c", "d"], doc.Lines.Select(l => l.Text));
    Assert.Equal(["\r\n", "\r\n", "\n", ""], doc.Lines.Select(l => l.Ending));
    Assert.Equal("\r\n", doc.DominantEnding);
    Assert.Equal(bytes, TextDocument.Encode(doc.HasBom, doc.Lines));
}
[Fact] public void Parse_EmptyFile_HasNoLines_AndDominantEndingLf() { /* Lines leer, DominantEnding "\n" */ }
[Fact] public void Parse_InvalidUtf8_ThrowsWithLineNumber() {
    byte[] bytes = [.. Encoding.UTF8.GetBytes("ok\n"), 0xC3, 0x28];
    Assert.Equal(2, Assert.Throws<ParseException>(() => TextDocument.Parse(bytes)).LineNumber);
}
[Fact] public void AtomicWrite_ReplacesExistingFile_AndLeavesNoTempFile() { /* danach nur noch die Zieldatei im Ordner */ }
```

- [ ] **Step 3: Fehlschlag prüfen.** `dotnet test tests/NoteEvolution.Core.Tests` → FAIL (Typen fehlen).
- [ ] **Step 4: Implementieren.** Dekodieren mit `new UTF8Encoding(false, throwOnInvalidBytes: true)` zeilenweise, damit die Zeilennummer bekannt ist. `DominantEnding` ist das häufigste Zeilenende, bei Gleichstand das zuerst gefundene, ohne Zeilenende `"\n"`. `AtomicFile`: temporäre Datei `.<name>.ne-tmp` im Zielordner, dann `File.Move(tmp, path, overwrite: true)`.
- [ ] **Step 5: Tests grün.** `dotnet test tests/NoteEvolution.Core.Tests` → PASS. `dotnet build NoteEvolution.slnx` → 0 Warnungen.
- [ ] **Step 6: Commit** `git add -A && git commit -m "feat(core): solution skeleton and lossless text document"`

---

### Task 2: Logseq-Parser und verlustfreier Serializer

**Files:**
- Create: `src/NoteEvolution.Core/Model/Page.cs`, `Block.cs`, `BlockProperty.cs`; `src/NoteEvolution.Core/Format/LogseqParser.cs`, `PageSerializer.cs`
- Test: `tests/NoteEvolution.Core.Tests/Format/LogseqParserTests.cs`, `PageRoundTripTests.cs`, `tests/NoteEvolution.Core.Tests/Samples.cs`

**Interfaces:**
- Consumes: `TextDocument`, `RawLine`, `ParseException` (Task 1).
- Produces:
  - `record BlockProperty(string Key, string Value)`
  - `sealed class Block { Guid Key /*Laufzeitschlüssel, nie geschrieben*/; char Bullet; string Indent; Block? Parent; IReadOnlyList<Block> Children; IReadOnlyList<RawLine> Lines /*eigene Zeilen: Anstrichzeile, Eigenschaften, Fortsetzungen, folgende Leerzeilen*/; IReadOnlyList<RawLine> BaseLines /*Stand beim Laden/Speichern*/; string Content; IReadOnlyList<BlockProperty> Properties; string? GetProperty(string key); Guid? Id; bool IsDirty; int SourceLineNumber; }`
  - `sealed class Page { string FilePath; string Name /*Dateiname ohne .md*/; bool HasBom; string NewLine; IReadOnlyList<RawLine> PrefixLines; IReadOnlyList<BlockProperty> PageProperties; string? GetPageProperty(string key); IReadOnlyList<Block> Roots; IEnumerable<Block> AllBlocks() /*Tiefensuche, Dateireihenfolge*/; bool IsReadOnly; string? ParseError; bool IsDirty; }`
  - `static class LogseqParser { static Page Parse(string filePath, byte[] bytes); }` wirft nie. Bei einem Fehler: `IsReadOnly = true`, `ParseError = "Zeile {n}: {grund}"`, die Blöcke so weit wie möglich befüllt.
  - `static class PageSerializer { static byte[] Serialize(Page page); }` wirft `InvalidOperationException` bei `IsReadOnly`.

Parse-Regeln (vom Plan festgelegt, wo die Spec offen ist):
- Anstrichzeile: `^(?<indent>[ \t]*)(?<bullet>[-*])( (?<text>.*))?$`. Einrückungsbreite: Tab = 4, Leerzeichen = 1. Stapelregel: Vom Stapel nehmen, solange die Breite oben ≥ der neuen Breite ist. Der verbleibende obere Block ist der Elternblock.
- Vor dem ersten Anstrich: `key:: value`-Zeilen werden Seiten-Eigenschaften. Alle Zeilen vor dem ersten Anstrich (auch andere Zeilen und Leerzeilen) bilden `PrefixLines`.
- Nach der Anstrichzeile: Die zusammenhängenden Zeilen `^\s*(?<key>[A-Za-z0-9_-]+)::( (?<value>.*))?$` sind Block-Eigenschaften. Danach folgen Fortsetzungszeilen: jede Zeile, die keine Anstrichzeile ist, sowie Leerzeilen.
- Codezäune: Eine Fortsetzungszeile, deren Text nach der Einrückung mit ```` ``` ```` beginnt, schaltet den Zaunmodus um. Im Zaunmodus ist jede Zeile Fortsetzung. Ist ein Zaun am Dateiende noch offen, gibt es einen Parse-Fehler mit der Zeile des öffnenden Zauns.
- `Content`: Text der Anstrichzeile plus die Fortsetzungszeilen ohne Eigenschaften. Jede Fortsetzung verliert das Präfix `Indent` und danach bis zu zwei Leerzeichen. Zusammengefügt mit `"\n"`, abschließende Leerzeilen weggelassen.
- `Id`: `id::`, falls als Guid parsebar, sonst `null`.

- [ ] **Step 1: Failing tests schreiben** (`Samples.cs` enthält die Beispieltexte als C#-Literale mit `\t`):

```csharp
[Fact] public void Parse_MixedTabsAndSpaces_BuildsTree() {
    var p = Parse("- a\n\t- b\n    - c\n- d");
    Assert.Equal(["a", "d"], p.Roots.Select(b => b.Content));
    Assert.Equal(["b", "c"], p.Roots[0].Children.Select(b => b.Content)); // 4 Leerzeichen = 1 Tab
}
[Fact] public void Parse_PageAndBlockProperties() {
    var p = Parse("title:: T\ntype:: book\n\n- x\n  id:: 6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70\n  collapsed:: true\n  zweite Zeile");
    Assert.Equal("book", p.GetPageProperty("type"));
    var b = p.Roots.Single();
    Assert.Equal("x\nzweite Zeile", b.Content);
    Assert.Equal(Guid.Parse("6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70"), b.Id);
    Assert.Equal("true", b.GetProperty("collapsed"));
}
[Fact] public void Parse_StarBulletAndHeading() { /* "* # Titel" → Bullet '*', Content "# Titel" */ }
[Fact] public void Parse_CodeFence_LinesInsideAreContent() {
    var p = Parse("- a\n  ```\n  - kein Block\n  ```\n- b");
    Assert.Equal(2, p.Roots.Count);
    Assert.Contains("- kein Block", p.Roots[0].Content);
}
[Fact] public void Parse_UnclosedFence_IsReadOnlyWithLine() {
    var p = Parse("- a\n  ```\n  - x");
    Assert.True(p.IsReadOnly);
    Assert.StartsWith("Zeile 2", p.ParseError);
}
[Theory, MemberData(nameof(Samples.RoundTrip), MemberType = typeof(Samples))]
public void RoundTrip_IsByteIdentical(string name, byte[] bytes) =>
    Assert.Equal(bytes, PageSerializer.Serialize(LogseqParser.Parse(name, bytes)));
```

  `Samples.RoundTrip` deckt ab: leere Datei, nur Seiten-Eigenschaften, Leerzeilen zwischen Blöcken, Leerzeichen am Zeilenende, CRLF, BOM, `*`- und `-`-Anstriche gemischt, Tabs und Leerzeichen gemischt, `collapsed::`, Tiefe 6, `[handled]`, `**fett**`, deutsche und englische Zeilen, Codezaun, Datei ohne Zeilenumbruch am Ende.
- [ ] **Step 2: Fehlschlag prüfen.** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~LogseqParser|FullyQualifiedName~PageRoundTrip"` → FAIL.
- [ ] **Step 3: Implementieren.** `PageSerializer` gibt `PrefixLines` aus und danach die `Lines` aller Blöcke in Tiefensuche. `BaseLines` ist beim Laden eine Kopie von `Lines`.
- [ ] **Step 4: Tests grün.** Gleicher Befehl → PASS.
- [ ] **Step 5: Commit** `feat(core): lossless Logseq parser and serializer`

---

### Task 3: Zeilengenaue Block-Operationen

**Files:**
- Modify: `src/NoteEvolution.Core/Model/Block.cs`, `Page.cs`
- Create: `src/NoteEvolution.Core/Model/IndentStyle.cs`
- Test: `tests/NoteEvolution.Core.Tests/Model/BlockEditTests.cs`, `tests/NoteEvolution.Core.Tests/LineDiff.cs`

**Interfaces:**
- Produces (jede Operation setzt `IsDirty` auf den betroffenen Blöcken):
  - `void Block.SetProperty(string key, string value)`: ersetzt eine vorhandene Zeile an Ort und Stelle. Sonst wird die Zeile nach der letzten Eigenschaftszeile eingefügt, ohne Eigenschaften direkt nach der Anstrichzeile. Format `Indent + "  " + key + ":: " + value`.
  - `void Block.RemoveProperty(string key)`
  - `void Block.SetContent(string content)`: Erste Zeile und Fortsetzungen werden zeilenweise verglichen. Nur abweichende Zeilen werden neu erzeugt; Eigenschaftszeilen bleiben unberührt.
  - `Guid Block.EnsureId()`
  - `static Block Block.CreateDetached(string content, IEnumerable<BlockProperty>? properties = null)`
  - `Block Block.CloneDetached(bool withoutProperties)` kopiert den ganzen Teilbaum.
  - `void Page.InsertBlock(Block? parent, int index, Block detached)` rendert die Zeilen mit dem Stil des Ziels.
  - `void Page.RemoveBlock(Block block)`
  - `void Page.MoveBlock(Block block, Block? newParent, int index)` rückt den ganzen Teilbaum neu ein: In jeder Zeile wird das alte `Indent`-Präfix durch das neue ersetzt.
  - `void Page.SetPageProperty(string key, string value)` setzt die Zeile hinter die letzte vorhandene Seiten-Eigenschaft, sonst an Zeile 1 mit einer folgenden Leerzeile.
  - `void Page.MarkSaved()` setzt `BaseLines = Lines` und `IsDirty = false` für alle Blöcke.
  - `static class IndentStyle { static string ChildIndent(Block? parent); static char BulletFor(Block? parent, int index); }`
- Einrückungsregel: Hat der Elternblock schon Kinder, wird das `Indent` des ersten Kindes übernommen. Sonst gilt `parent.Indent + Einheit`. Die Einheit ist `parent.Indent` ohne das Einrückungs-Präfix des Großelternblocks; ist das leer, gilt `"\t"`. Wurzel: `""`. Anstrichzeichen: das des Nachbarn, sonst das des Elternblocks, sonst `'-'`. Neue Zeilen erhalten `Page.NewLine`; ist die bisher letzte Zeile ohne Zeilenende, bekommt sie `Page.NewLine`.

- [ ] **Step 1: Failing tests schreiben.** Der Helfer `LineDiff.Changed(byte[] before, byte[] after) -> (IReadOnlyList<int> removed, IReadOnlyList<int> added)` vergleicht Zeilen.

```csharp
[Fact] public void SetProperty_New_AddsExactlyOneLineAfterLastProperty() {
    var p = Parse("- a\n  collapsed:: true\n  rest\n\t- b");
    p.Roots[0].SetProperty("used-in", "[[Buch - X]]");
    Assert.Equal("- a\n  collapsed:: true\n  used-in:: [[Buch - X]]\n  rest\n\t- b", Text(p));
}
[Fact] public void SetProperty_Existing_ReplacesInPlace() { /* used-in-Zeile ersetzt, Zeilenzahl gleich */ }
[Fact] public void SetContent_OnlyDifferingLinesChange() {
    var before = "- a\n\t  zwei\n  id:: …"; // Fortsetzung mit Tab-Präfix bleibt, wenn der Text gleich ist
}
[Fact] public void InsertBlock_IntoTabIndentedParent_UsesTabs_AndCrlf() {
    var p = Parse("- a\r\n\t- b\r\n");
    p.InsertBlock(p.Roots[0].Children[0], 0, Block.CreateDetached("neu"));
    Assert.Equal("- a\r\n\t- b\r\n\t\t- neu\r\n", Text(p));
}
[Fact] public void InsertBlock_IntoSpaceIndentedParent_UsesSpaces() { /* "- a\n  - b" → Kind von b mit 4 Leerzeichen */ }
[Fact] public void InsertBlock_AfterLastLineWithoutEnding_AddsEnding() { }
[Fact] public void MoveBlock_ReindentsSubtree_OtherLinesUnchanged() { }
[Fact] public void EnsureId_ReturnsExisting_OrAddsLowercaseUuid() { /* Regex ^[0-9a-f-]{36}$ */ }
[Fact] public void CloneDetached_WithoutProperties_CopiesChildrenContentOnly() { }
[Fact] public void SetPageProperty_NoProperties_InsertsFirstLineAndBlankLine() { /* "- a" → "type:: book\n\n- a" */ }
```

- [ ] **Step 2: Fehlschlag prüfen.** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~BlockEdit"` → FAIL.
- [ ] **Step 3: Implementieren** gemäß den Interfaces.
- [ ] **Step 4: Tests grün.** Gleicher Befehl und der gesamte Core-Testlauf → PASS.
- [ ] **Step 5: Commit** `feat(core): line-preserving block operations`

---

### Task 4: Wertformate und Inline-Markdown

**Files:**
- Create: `src/NoteEvolution.Core/Links/UsedInValue.cs`, `SourceValue.cs`; `src/NoteEvolution.Core/Text/InlineMarkdown.cs`, `NoteTag.cs`
- Test: `tests/NoteEvolution.Core.Tests/Text/ValueFormatTests.cs`, `InlineMarkdownTests.cs`

**Interfaces:**
- Produces:
  - `record UsedInEntry(string PageName, Guid? BookBlockId)`; `static class UsedInValue { IReadOnlyList<UsedInEntry> Parse(string value); string Format(IEnumerable<UsedInEntry> entries); }`, Format `[[Name]] ((id))` bzw. `[[Name]]`, verbunden mit `", "`.
  - `static class SourceValue { IReadOnlyList<Guid> Parse(string value); string Format(IEnumerable<Guid> ids); }`, Format `((id)), ((id))`.
  - `record InlineRun(string Text, bool Bold, bool Italic)`; `static class InlineMarkdown { IReadOnlyList<InlineRun> Parse(string text); string Format(IEnumerable<InlineRun> runs); }`. Gelesen werden `**x**`, `*x*` und `_x_`; geschrieben wird `**` bzw. `*`. Ein nicht geschlossener Marker bleibt Text.
  - `static class NoteTag { const string Tag = "#notiz"; bool Has(string content); string Strip(string content); string Add(string text); }`. `Has` prüft `(^|\s)#notiz(\s|$)`. `Add` hängt `" #notiz"` an.

- [ ] **Step 1: Failing tests schreiben**

```csharp
[Fact] public void UsedIn_ParsesEntriesWithAndWithoutBlockRef() {
    var e = UsedInValue.Parse("[[Buch - LoveMagic]] ((6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70)), [[Buch - X]]");
    Assert.Equal(new UsedInEntry("Buch - LoveMagic", Guid.Parse("6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70")), e[0]);
    Assert.Equal(new UsedInEntry("Buch - X", null), e[1]);
}
[Fact] public void UsedIn_FormatRoundTrips() { }
[Fact] public void Source_ParsesCommaSeparatedRefs_IgnoresGarbage() { /* "((a)), kaputt, ((b))" → 2 Guids */ }
[Fact] public void Inline_BoldItalicMixed() {
    Assert.Equal([new("a ", false, false), new("fett", true, false), new(" und ", false, false), new("kursiv", false, true)],
                 InlineMarkdown.Parse("a **fett** und _kursiv_"));
}
[Fact] public void Inline_UnclosedMarker_StaysText() { /* "5 * 3" → ein Lauf "5 * 3" */ }
[Fact] public void NoteTag_HasStripAdd() { /* "x #notiz" → Has true, Strip "x"; "#notizen" → Has false */ }
```

- [ ] **Step 2–4:** Fehlschlag prüfen, implementieren, grün. Befehl: `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~ValueFormat|FullyQualifiedName~InlineMarkdown"`.
- [ ] **Step 5: Commit** `feat(core): used-in/source values and inline markdown`

---

### Task 5: Buchmodell

**Files:**
- Create: `src/NoteEvolution.Core/Books/Book.cs`, `OutlineNode.cs`, `TextBlock.cs`, `Paragraph.cs`
- Test: `tests/NoteEvolution.Core.Tests/Books/BookModelTests.cs`

**Interfaces:**
- Consumes: `Page`, `Block` (Task 2), `SourceValue`, `NoteTag` (Task 4).
- Produces:
  - `sealed class Book { Page Page; string LinkName /*= Page.Name*/; string Title /*title:: oder Page.Name*/; string? Dedication; OutlineNode Root; IReadOnlyList<OutlineWarning> Warnings; static bool IsBook(Page page) /*type:: book, ohne Rücksicht auf Groß-/Kleinschreibung*/; static Book Load(Page page); OutlineNode? FindNode(Guid key); TextBlock? FindTextBlock(Guid key); TextBlock? FindTextBlockById(Guid id); }`
  - `abstract class BookItem`; `sealed class OutlineNode : BookItem { Guid Key /*Block.Key, Root: Guid.Empty*/; Block? Block; string Title; int Level /*Root 0*/; IReadOnlyList<BookItem> Items /*Dateireihenfolge*/; IEnumerable<OutlineNode> Children; IEnumerable<TextBlock> TextBlocks; int WordCount; int SourceCount; OutlineNode? Parent; }`
  - `sealed class TextBlock : BookItem { Block Block; Guid Key; string Text; IReadOnlyList<Paragraph> Paragraphs; IReadOnlyList<Guid> Sources; OutlineNode Section; }`
  - `record Paragraph(Block Block, string Text, int Depth /*1 = direktes Kind*/, bool IsNote)`
  - `record OutlineWarning(Guid BlockKey, string Message)`
- Regeln: Eine Überschrift ist ein Block, dessen `Content` mit `#{1,6} ` beginnt; der Titel ist der Rest. Nicht-Überschriften unter einer Überschrift oder unter der Wurzel sind `TextBlock`s; ihre Nachkommen werden zu `Paragraph`s in Tiefensuche, auch eingebettete `#`-Zeilen, die dann eine Warnung auslösen. Ist eine Kind-Überschrift nicht tiefer als ihr Elternknoten, entsteht eine Warnung „Überschrift ‚X‘ ist nicht tiefer als ‚Y‘“. `WordCount` zählt die durch Leerraum getrennten Wörter in `Text` und in den Absätzen ohne `IsNote` im ganzen Teilbaum. `SourceCount` zählt die verschiedenen Quellen im Teilbaum.

- [ ] **Step 1: Failing tests schreiben.** Grundlage ist das Buchbeispiel aus Spec 3.2 (mit Tab-Einrückung) plus Vorspann und Warnfall:

```csharp
[Fact] public void Load_SpecExample_BuildsOutlineAndTextBlocks() {
    var b = Book.Load(Parse(Samples.SpecBook));
    var part = b.Root.Children.Single();
    Assert.Equal(("Liebe und Wahrheit", 1), (part.Title, part.Level));
    var tb = part.Children.Single().TextBlocks.Single();
    Assert.Equal("Angst baut Widerstand auf, Vertrauen baut Schwung auf.", tb.Text);
    Assert.Equal(2, tb.Sources.Count);
    Assert.Equal([false, true], tb.Paragraphs.Select(p => p.IsNote));
    Assert.Equal("noch ein Beispiel ergänzen", tb.Paragraphs[1].Text);
}
[Fact] public void Load_TextBeforeFirstHeading_IsPrologueUnderRoot() { }
[Fact] public void Load_ChildHeadingNotDeeper_AddsWarning_WritesNothing() { /* Page.IsDirty bleibt false */ }
[Fact] public void Load_TextAfterSubsection_KeepsFileOrderInItems() { }
[Fact] public void WordCount_ExcludesNotes_SourceCountDistinct() { }
[Fact] public void IsBook_DetectsTypeBook() { }
```

- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~BookModel"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(core): book model with outline, text blocks and warnings`

---

### Task 6: Vault und Notizmodell

**Files:**
- Create: `src/NoteEvolution.Core/Vaults/IVault.cs`, `Vault.cs`, `VaultSettings.cs`, `NoteBlock.cs`, `INoteRepository.cs`, `NoteRepository.cs`
- Test: `tests/NoteEvolution.Core.Tests/Vaults/VaultTests.cs`, `NoteRepositoryTests.cs`, `tests/NoteEvolution.Core.Tests/TestVault.cs`

**Interfaces:**
- Consumes: `LogseqParser`, `Book`, `UsedInValue`.
- Produces:
  - `sealed class VaultSettings { List<string> NoteFolders /*Standard ["journals","pages"]*/; static VaultSettings Load(string root); void Save(string root); }` in der Datei `.noteevolution/settings.json`.
  - `interface IVault { string Root; VaultSettings Settings; IReadOnlyList<Page> Pages; IReadOnlyList<Book> Books; Page? FindPageByPath(string path); Book? FindBook(string linkName) /*ohne Rücksicht auf Groß-/Kleinschreibung*/; (Page Page, Block Block)? FindBlockById(Guid id); (Page Page, Block Block)? FindBlockByKey(Guid key); void ReplacePage(Page page) /*auch Hinzufügen*/; void RemovePage(string path); event Action<Page>? PageReplaced; }`
  - `sealed class Vault : IVault { static Vault Open(string root); }` liest alle `*.md` rekursiv in den `NoteFolders`.
  - `sealed class NoteBlock { Page Page; Block Block; Guid Key; DateOnly? Date; IReadOnlyList<string> ContextPath /*Inhalte der Vorfahren, erste Zeile*/; IReadOnlyList<UsedInEntry> Usages /*eigene*/; bool IsUsed /*selbst oder ein Vorfahr hat used-in::*/; }`
  - `record NoteFilter(bool HideUsed, DateOnly? From, DateOnly? To)`
  - `interface INoteRepository { IEnumerable<NoteBlock> All(); NoteBlock? Get(Guid key); IReadOnlyList<NoteBlock> Journal(NoteFilter filter); bool Matches(NoteBlock note, NoteFilter filter); }`. `Journal` liefert die Wurzelblöcke der Journalseiten, neueste zuerst, innerhalb eines Tages in Dateireihenfolge. Ein Datumsfilter schließt Blöcke ohne Datum aus.
  - Datum: Dateiname `yyyy_MM_dd.md` in einem Ordner namens `journals`, sonst `null`. Buchseiten sind keine Notizen.
  - Test-Helfer `sealed class TestVault : IDisposable { string Root; static TestVault Create(params (string Path, string Content)[] files); Vault Open(); string Read(string relPath); }`

- [ ] **Step 1: Failing tests schreiben**

```csharp
[Fact] public void Open_ReadsNoteFolders_SeparatesBooks() { /* journals/2026_03_01.md, pages/Buch - Test.md (type:: book), pages/Idee.md → Books 1, Notizen ohne Buchblöcke */ }
[Fact] public void Open_CustomNoteFolders_FromSettings() { }
[Fact] public void NoteBlock_DateFromJournalFileName_NullForPages() { }
[Fact] public void NoteBlock_IsUsed_WhenAncestorHasUsedIn() { }
[Fact] public void Journal_HideUsedAndDateRange_NewestFirst() { }
[Fact] public void FindBlockById_FindsAcrossPages() { }
[Fact] public void Open_UnparsableFile_IsListedReadOnly() { }
```

- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~Vault|FullyQualifiedName~NoteRepository"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(core): vault loading and note model`

---

### Task 7: Speichern, Sicherungen, eigene Schreibvorgänge

**Files:**
- Create: `src/NoteEvolution.Core/Storage/IClock.cs`, `IPageWriter.cs`, `PageWriter.cs`, `BackupService.cs`, `SelfWriteRegistry.cs`
- Test: `tests/NoteEvolution.Core.Tests/Storage/PageWriterTests.cs`, `BackupServiceTests.cs`

**Interfaces:**
- Consumes: `PageSerializer`, `AtomicFile`, `Page.MarkSaved`.
- Produces:
  - `interface IClock { DateTimeOffset Now { get; } }`, `SystemClock`, in Tests `FakeClock`
  - `interface IPageWriter { void Save(Page page); }`. Wirft `ReadOnlyPageException` bei `IsReadOnly`, sonst `IOException` nach 3 Versuchen im Abstand von 200 ms.
  - `sealed class PageWriter(IVault vault, BackupService backups, SelfWriteRegistry registry) : IPageWriter`. Ablauf: tägliche Sicherung → serialisieren → atomar schreiben → `registry.Record` → `page.MarkSaved()`.
  - `sealed class BackupService(string vaultRoot, IClock clock) { void EnsureDailyBackup(string path); string CreateBackup(string path); void Cleanup(); }`. Tägliche Sicherung unter `.noteevolution/backups/yyyy-MM-dd/<relativer Pfad>`, nur falls dort noch keine Datei liegt und die Quelldatei existiert. `CreateBackup` (erzwungen) legt `<relativer Pfad>.HHmmss.bak` im selben Tagesordner an. `Cleanup` löscht Tagesordner, die älter als 30 Tage sind.
  - `sealed class SelfWriteRegistry { void Record(string path, byte[] bytes); bool IsOwnWrite(string path, byte[] currentBytes); }` vergleicht SHA-256-Werte.

- [ ] **Step 1: Failing tests schreiben**

```csharp
[Fact] public void Save_FirstWriteOfDay_CreatesBackupOnce() { /* zweiter Save am selben Tag ändert die Sicherung nicht */ }
[Fact] public void Save_ReadOnlyPage_Throws_FileUntouched() { }
[Fact] public void Save_ClearsDirty_AndUpdatesBaseLines() { }
[Fact] public void Save_RecordsOwnWrite() { }
[Fact] public void Cleanup_DeletesFoldersOlderThan30Days() { /* 31 Tage alt → weg, 30 Tage → bleibt */ }
[Fact] public void CreateBackup_Forced_AddsTimestampedCopy() { }
```

- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~PageWriter|FullyQualifiedName~BackupService"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(core): atomic page writer with daily backups`

---

### Task 8: Übernehmen, Quelle entfernen, Löschen, Rückgängig, pending.json

**Files:**
- Create: `src/NoteEvolution.Core/Links/ILinkService.cs`, `LinkService.cs`, `InsertPosition.cs`, `PendingStore.cs`, `UndoManager.cs`
- Test: `tests/NoteEvolution.Core.Tests/Links/AdoptTests.cs`, `RemoveAndDeleteTests.cs`, `PendingTests.cs`

**Interfaces:**
- Consumes: `IVault`, `IPageWriter`, `Book`, `UsedInValue`, `SourceValue`, Block-Operationen (Task 3).
- Produces:
  - `abstract record InsertPosition { record After(Guid TextBlockKey); record SectionStart(Guid SectionKey); record SectionEnd(Guid SectionKey); }`. `SectionKey == Guid.Empty` bezeichnet den Vorspann. `SectionStart` meint die Stelle vor dem ersten `TextBlock` des Abschnitts, `SectionEnd` die Stelle nach dem letzten `TextBlock` und vor der ersten Unter-Überschrift, die darauf folgt.
  - `record AdoptResult(Guid TextBlockKey, Guid TextBlockId, bool NoteUpdatePending)`
  - `interface ILinkService { AdoptResult Adopt(Book book, Guid noteBlockKey, InsertPosition position); void RemoveSource(Book book, Guid textBlockKey, Guid noteBlockId); void DeleteTextBlock(Book book, Guid textBlockKey); void AddUsage(Guid noteBlockId, UsedInEntry entry); void RemoveUsage(Guid noteBlockId, string bookLinkName, Guid? bookBlockId); int RetryPending(); }`. Task 9 ergänzt `ApplySyncEffects`.
  - `sealed class LinkService(IVault vault, IPageWriter writer, UndoManager undo, PendingStore pending) : ILinkService`. Die Methoden verändern `book.Page`; Aufrufer erzeugen die Sicht danach mit `Book.Load(book.Page)` neu.
  - `interface IUndoAction { string Description { get; } void Undo(); }`; `sealed class UndoManager { void Push(IUndoAction a); bool CanUndo; string? NextDescription; void Undo(); event Action? Changed; }`. Auf dem Stapel liegen höchstens 50 Aktionen.
  - `record NoteLocator(string PagePath, int[] TreePath, string ContentSha256)`; `record PendingNoteUpdate(Guid NoteBlockId, NoteLocator Locator, string BookLinkName, Guid BookBlockId, bool Remove)`; `sealed class PendingStore(string vaultRoot) { IReadOnlyList<PendingNoteUpdate> Load(); void Add(PendingNoteUpdate u); void Remove(PendingNoteUpdate u); }` in der Datei `.noteevolution/pending.json`.
- Ablauf `Adopt` (Spec 5.2): `note.EnsureId()` (nur im Speicher) → Teilbaum mit `CloneDetached(withoutProperties: true)` kopieren → an der Position einfügen → `EnsureId()` und `source:: ((noteId))` setzen → **Buch speichern** (scheitert das, wird die Buchseite aus der Datei neu geladen und die Ausnahme weitergegeben) → `used-in` der Notiz ergänzen und Notiz speichern (scheitert das, kommt ein `PendingStore.Add`, und es gilt `NoteUpdatePending = true`) → Undo-Aktion „Übernehmen“ ablegen. Diese entfernt den Buchblock und den `used-in`-Eintrag; `id::` bleibt.
- `RetryPending`: Notiz über `NoteBlockId` suchen, sonst über `Locator` (Baumpfad und Inhalts-Hash müssen passen) und dann die ID setzen. Gelingt das nicht, bleibt der Eintrag stehen. Rückgabe ist die Zahl der erledigten Einträge. Aufgerufen wird `RetryPending` beim Start und nach jedem `Save`.

- [ ] **Step 1: Failing tests schreiben** (alle mit `TestVault`, Buchdatei `pages/Buch - Test.md`):

```csharp
[Fact] public void Adopt_AfterBlock_CopiesSubtreeWithoutProperties_LinksBothSides() {
    // Notiz "- Kern\n  collapsed:: true\n\t- Detail"
    var r = links.Adopt(book, noteKey, new InsertPosition.After(tbKey));
    Assert.Contains($"\t\t- Kern\n\t\t  id:: {r.TextBlockId}\n\t\t  source:: (({noteId}))\n\t\t\t- Detail", vault.Read("pages/Buch - Test.md"));
    Assert.Contains($"used-in:: [[Buch - Test]] (({r.TextBlockId}))", vault.Read("journals/2026_03_01.md"));
    Assert.DoesNotContain("collapsed", BookBlockText(r));
}
[Fact] public void Adopt_SectionEnd_InsertsBeforeFollowingSubheading() { }
[Fact] public void Adopt_ChildBulletOnly_LinksChild() { }
[Fact] public void Adopt_Twice_UsedInHasTwoEntries() { }                      // Review Focus 4
[Fact] public void RemoveSource_OneOfTwoUsages_KeepsOther() { }               // Review Focus 4
[Fact] public void DeleteTextBlock_RemovesUsedIn_UndoRestoresBothFiles() { /* Bytes beider Dateien wie vorher, abgesehen von id:: */ }
[Fact] public void Undo_Adopt_RemovesBookBlockAndUsage() { }
[Fact] public void Adopt_NoteWriteFails_RecordsPending_RetryCompletes() { /* IPageWriter-Fake, der für den Notizpfad einmal wirft */ }
[Fact] public void Retry_AfterRestart_LocatesNoteByTreePathAndHash() { }
```

- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~Core.Tests.Links"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(core): adopt, remove source, delete with undo and pending queue`

---

### Task 9: Editor-Abgleich (BookSnapshot und BookSync)

**Files:**
- Create: `src/NoteEvolution.Core/Books/BookSnapshot.cs`, `BookSync.cs`
- Modify: `src/NoteEvolution.Core/Links/LinkService.cs` (`ApplySyncEffects`)
- Test: `tests/NoteEvolution.Core.Tests/Books/BookSyncTests.cs`

**Interfaces:**
- Consumes: `Book`, Block-Operationen, `NoteTag`, `IVault` (für Quellen-Beschriftungen).
- Produces:
  - `record SourceInfo(Guid NoteId, string Label, bool IsBroken)`. `Label` ist das Notizdatum `yyyy-MM-dd` bzw. der Seitenname, dazu die ersten 40 Zeichen des Inhalts. `IsBroken` gilt, wenn `vault.FindBlockById` nichts findet.
  - `record SectionSnapshot(Guid ScopeKey, bool IncludeSubsections, IReadOnlyList<SnapshotNode> Nodes)`
  - `abstract record SnapshotNode(Guid Key)`; `record SnapshotHeading(Guid Key, int Level, string Text) : SnapshotNode`; `record SnapshotTextBlock(Guid Key, Guid? SplitFrom, string Text, IReadOnlyList<SnapshotParagraph> Paragraphs, IReadOnlyList<SourceInfo> Sources) : SnapshotNode`; `record SnapshotParagraph(Guid Key, string Text, int Depth, bool IsNote)`. `Text` ist Inline-Markdown, Zeilenumbrüche im Block stehen als `"\n"`.
  - `static class BookSnapshot { SectionSnapshot Create(Book book, IVault vault, Guid scopeKey, bool includeSubsections); }`. Abschnittsansicht (`false`): nur die `TextBlock`s des Knotens, keine Überschriften. Manuskriptansicht (`true`): Teilbaum in Dateireihenfolge mit Überschriften; die Überschrift des Bereichs selbst ist das erste Element.
  - `abstract record SyncEffect; record BlockDeleted(Guid BookBlockId, IReadOnlyList<Guid> Sources) : SyncEffect; record BlockSplit(Guid NewBookBlockId, IReadOnlyList<Guid> Sources) : SyncEffect;`
  - `record SyncResult(bool Changed, IReadOnlyList<SyncEffect> Effects)`; `static class BookSync { SyncResult Apply(Book book, SectionSnapshot snapshot); }`
- Abgleichsregeln:
  1. Manuskript: Die Snapshot-Folge wird an Überschriften in Segmente geteilt. Jedes Segment wird wie eine Abschnittsansicht mit dieser Überschrift als Bereich abgeglichen. Überschriften: Der Text wird bei Abweichung übernommen (`#`-Präfix der vorhandenen Ebene bleibt). Überschriften ohne bekannten Schlüssel werden ignoriert, fehlende Überschriften bleiben bestehen. Die Gliederung wird nur über Task 10 verändert.
  2. Textblock mit bekanntem Schlüssel: `SetContent`, wenn sich `Text` unterscheidet. Gehört er bisher zu einem anderen Abschnitt, wird er mit `MoveBlock` verschoben.
  3. Unbekannter Schlüssel mit `SplitFrom` eines bekannten verknüpften Blocks: neuer Block mit eigener ID und denselben `source::`, Effekt `BlockSplit`. Unbekannter Schlüssel sonst: neuer Block ohne ID. Ein neuer Block übernimmt den Snapshot-Schlüssel als `Block.Key`.
  4. Reihenfolge: Ein Block bleibt, wo er ist, wenn er in `Items` hinter seinem Snapshot-Vorgänger steht. Sonst wird er direkt dahinter gesetzt. Ein neuer erster Block kommt vor den ersten vorhandenen `TextBlock`, sonst vor die erste Unter-Überschrift, sonst ans Ende.
  5. Bekannte Blöcke im Bereich, die im Snapshot fehlen, werden entfernt, Effekt `BlockDeleted`, wenn Quellen vorhanden sind.
  6. Absätze: Die Tiefen bauen den Kinderbaum auf. Ein Sprung um mehr als 1 wird auf die vorherige Tiefe + 1 begrenzt. Bekannte Schlüssel werden verschoben oder geändert, unbekannte neu angelegt, fehlende entfernt. `IsNote` wird über `NoteTag.Add`/`Strip` geschrieben. Ein Block, dessen `(Text, IsNote)` gleich bleibt, wird nicht angefasst.
  - Neu in `ILinkService`: `void ApplySyncEffects(Book book, IReadOnlyList<SyncEffect> effects)`. `BlockSplit` → `AddUsage` für jede Quelle; `BlockDeleted` → `RemoveUsage` und eine Undo-Aktion „Löschen“.

- [ ] **Step 1: Failing tests schreiben**

```csharp
[Fact] public void Apply_UnchangedSnapshot_NotChanged_BytesIdentical() { }
[Fact] public void Apply_TextEdit_ChangesOnlyThatLine() { }
[Fact] public void Apply_NewKeyAfterExisting_InsertsWithoutId() { }
[Fact] public void Apply_SplitOfLinkedBlock_NewIdSameSources_EffectSplit() { }
[Fact] public void ApplySyncEffects_Split_AddsSecondUsageToNote() { }
[Fact] public void Apply_MissingLinkedBlock_RemovedWithDeleteEffect() { }
[Fact] public void Apply_ParagraphDepths_BuildChildTree_ClampsJumps() { }
[Fact] public void Apply_ToggleNote_AddsAndStripsTag() { }
[Fact] public void Apply_SectionView_TextAfterSubsection_StaysWhenUntouched() { }
[Fact] public void Apply_Manuscript_BlockMovedUnderOtherHeading_Reindented() { }
[Fact] public void Apply_Manuscript_HeadingRenamed_LevelKept_MissingHeadingKept() { }
[Fact] public void Create_ThenApply_RoundTripsSpecExample() { }
```

- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~BookSync"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(core): editor snapshot reconciliation with split/delete effects`

---

### Task 10: Gliederungsoperationen

**Files:**
- Create: `src/NoteEvolution.Core/Books/OutlineEditor.cs`
- Test: `tests/NoteEvolution.Core.Tests/Books/OutlineEditorTests.cs`

**Interfaces:**
- Produces: `static class OutlineEditor { Guid AddHeading(Book book, Guid parentKey, string title); void Rename(Book book, Guid nodeKey, string title); void MoveSection(Book book, Guid nodeKey, Guid newParentKey, int index); }`. Bei `AddHeading` wird die Ebene des Elternknotens + 1 als Rautenzahl verwendet; der neue Knoten kommt ans Ende der Kinder. `MoveSection` verschiebt den Teilbaum und setzt die Rautenzahl jeder Überschrift darin auf neue Ebene + relative Tiefe. `index` zählt nur unter den `OutlineNode`-Kindern.

- [ ] **Step 1: Failing tests:** `AddHeading_UsesParentLevelPlusOne`, `Rename_KeepsHashesAndProperties` (`collapsed::` bleibt), `MoveSection_RecomputesLevelsInSubtree`, `MoveSection_OtherLinesUnchanged`.
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~OutlineEditor"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(core): outline add/rename/move`

---

### Task 11: Verknüpfungsprüfung

**Files:**
- Create: `src/NoteEvolution.Core/Links/LinkChecker.cs`
- Test: `tests/NoteEvolution.Core.Tests/Links/LinkCheckerTests.cs`

**Interfaces:**
- Consumes: `IVault`, `ILinkService.AddUsage/RemoveUsage`, `IPageWriter`.
- Produces:
  - `record MissingUsage(Guid NoteBlockId, UsedInEntry Entry)`; `record OrphanUsage(Guid NoteBlockKey, UsedInEntry Entry)`; `record BrokenSource(string BookLinkName, Guid TextBlockKey, Guid MissingNoteId)`
  - `record LinkCheckReport(IReadOnlyList<MissingUsage> Missing, IReadOnlyList<OrphanUsage> Orphans, IReadOnlyList<BrokenSource> Broken)`
  - `enum OrphanResolution { MarkUnknown, Remove }`
  - `sealed class LinkChecker(IVault vault, ILinkService links) { LinkCheckReport Analyze(); int FixMissing(LinkCheckReport report); void Resolve(OrphanUsage orphan, OrphanResolution resolution); }`. Waise ist ein Eintrag mit Blockverweis, dessen Buch oder Buchblock nicht existiert, und ebenso ein Eintrag, dessen Buch nicht existiert. `MarkUnknown` ersetzt den Eintrag durch `[[Name]]` ohne Blockverweis.

- [ ] **Step 1: Failing tests:** `Analyze_SourceWithoutUsedIn_ReportedAndFixed`, `Analyze_UsedInPointingToMissingBlock_IsOrphan`, `Resolve_MarkUnknown_KeepsPageLinkOnly`, `Resolve_Remove_DeletesEntryAndEmptyProperty`, `Analyze_SourceToMissingNote_IsBroken`, `Analyze_CleanVault_EmptyReport`.
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~LinkChecker"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(core): link consistency check`

---

### Task 12: Dateiüberwachung und Konflikte

**Files:**
- Create: `src/NoteEvolution.Core/Storage/VaultWatcher.cs`, `ConflictDetector.cs`, `PageMerger.cs`, `RuntimeKeys.cs`, `ExternalChangeHandler.cs`
- Test: `tests/NoteEvolution.Core.Tests/Storage/ConflictTests.cs`, `ExternalChangeTests.cs`, `VaultWatcherTests.cs`

**Interfaces:**
- Consumes: `IVault`, `LogseqParser`, `SelfWriteRegistry`, Block-Operationen.
- Produces:
  - `sealed class VaultWatcher(IVault vault, SelfWriteRegistry registry) : IDisposable { event Action<string>? ExternalChange; void Start(); }`. Grundlage ist `FileSystemWatcher` (rekursiv, `*.md`, nur in den `NoteFolders`). Ereignisse werden pro Pfad 300 ms entprellt; eigene Schreibvorgänge und `.noteevolution/` werden ignoriert.
  - `static class RuntimeKeys { void Carry(Page from, Page to); }` überträgt `Block.Key` nach `id::`, sonst nach gleichem Baumpfad mit gleichen `Lines`.
  - `record BlockConflict(Block Local, Block? External)`; `static class ConflictDetector { IReadOnlyList<BlockConflict> Detect(Page local, Page external); }`. Ein Konflikt liegt vor, wenn ein lokal geänderter Block (mit nicht leeren `BaseLines`) extern andere `Lines` als seine `BaseLines` hat oder extern fehlt. Zugeordnet wird über `id::`, sonst über den Baumpfad.
  - `enum ConflictChoice { Mine, Theirs, Both }`; `static class PageMerger { Page Merge(Page local, Page external, IReadOnlyDictionary<Guid, ConflictChoice> choices); }`. Grundlage ist die externe Fassung. Nicht-konfliktbehaftete lokale Änderungen werden erneut angewendet. Lokal neue Blöcke kommen hinter ihren zugeordneten Vorgänger. `Mine` ersetzt durch die lokale Fassung, `Both` setzt den externen Block als zusätzlichen Geschwisterblock direkt hinter den lokalen.
  - `abstract record ExternalChangeOutcome { record Reloaded(Page Page); record Removed(string Path); record Conflict(Page Local, Page External, IReadOnlyList<BlockConflict> Conflicts); }`; `sealed class ExternalChangeHandler(IVault vault) { ExternalChangeOutcome Handle(string path); void Resolve(ExternalChangeOutcome.Conflict c, IReadOnlyDictionary<Guid, ConflictChoice> choices); }`. Ohne lokale Änderungen wird still neu geladen und die Schlüssel werden übertragen. Ist die externe Fassung nicht parsebar, wird sie als schreibgeschützte Seite geladen, und die lokalen Änderungen werden als Konflikt mit `External = null` gemeldet.

- [ ] **Step 1: Failing tests schreiben**

```csharp
[Fact] public void Handle_NoLocalChanges_ReloadsSilently_KeepsKeys() { }   // Review Focus 5
[Fact] public void Handle_ExternalEditToBook_ThenSync_NoDuplicateBlocks() { /* Snapshot vor dem Neuladen, Apply danach → Blockzahl unverändert */ }
[Fact] public void Detect_LocalDirtyAndExternalChangedSameBlock_IsConflict() { }
[Fact] public void Detect_DifferentBlocksChanged_NoConflict_MergeKeepsBoth() { }
[Fact] public void Merge_Both_InsertsExternalAfterLocal() { }
[Fact] public void Handle_OwnWrite_IsIgnoredByWatcher() { }
[Fact] public void Handle_DeletedFile_RemovesPage() { }
```

- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~Conflict|FullyQualifiedName~ExternalChange|FullyQualifiedName~VaultWatcher"`: FAIL → implementieren → PASS. Der Watcher-Test wartet höchstens 5 s auf das Ereignis.
- [ ] **Step 5: Commit** `feat(core): file watching, key carry-over and conflict merge`

---

### Task 13: Assistent `[handled]`

**Files:**
- Create: `src/NoteEvolution.Core/Assistants/HandledConverter.cs`
- Test: `tests/NoteEvolution.Core.Tests/Assistants/HandledConverterTests.cs`

**Interfaces:**
- Produces: `sealed class HandledConverter(INoteRepository notes, IPageWriter writer) { IReadOnlyList<NoteBlock> Find(); void Apply(IEnumerable<NoteBlock> selected, Book book); }`. `Find` liefert die Blöcke, deren `Content` mit `[handled]` beginnt (Groß-/Kleinschreibung beachtet). `Apply` entfernt `[handled]` und genau ein folgendes Leerzeichen, setzt `used-in:: [[<book.LinkName>]]` (bzw. ergänzt den Eintrag) und speichert jede Seite einmal.

- [ ] **Step 1: Failing tests:** `Find_ListsHandledBlocksIncludingChildren`, `Apply_StripsPrefix_AddsUsedInWithoutBlockRef`, `Apply_OnlySelected_OthersUntouched`, `Apply_ExistingUsedIn_AppendsEntry`, `Apply_OtherLinesByteIdentical`.
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~HandledConverter"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(core): [handled] conversion assistant`

---

### Task 14: Assistent Entwurf → Mischformat

**Files:**
- Create: `src/NoteEvolution.Core/Assistants/DraftConverter.cs`
- Test: `tests/NoteEvolution.Core.Tests/Assistants/DraftConverterTests.cs`

**Interfaces:**
- Produces: `sealed class DraftNode { Block Block; bool IsHeading { get; set; } bool IsFixed /*beginnt schon mit #*/; IReadOnlyList<DraftNode> Children; }`; `sealed class DraftProposal { IReadOnlyList<DraftNode> Roots; int LevelOf(DraftNode node) /*1 + Zahl der Überschriften-Vorfahren*/; }`; `sealed class DraftConverter(BackupService backups, IPageWriter writer) { DraftProposal Propose(Page page); void Apply(Page page, DraftProposal proposal); }`
- Heuristik (Spec 5.6): `IsHeading = true`, wenn der Block Kinder hat, einzeilig ist, höchstens 80 Zeichen hat (getrimmt) und nicht auf `.`, `!`, `?` oder `…` endet. `Apply`: zuerst `backups.CreateBackup`, dann bei jedem nicht festen Überschriftenknoten `"#" * Level + " "` vor die erste Zeile setzen, dann `SetPageProperty("type", "book")`, dann speichern.

- [ ] **Step 1: Failing tests:** `Propose_HeuristicPicksShortParentsWithoutSentenceEnd`, `Propose_LevelsFollowHeadingAncestors`, `Toggle_ChangesLevelsBelow`, `Apply_CreatesBackupFirst`, `Apply_KeepsCollapsedAndOtherLines`, `Apply_AddsTypeBook`.
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Core.Tests --filter "FullyQualifiedName~DraftConverter"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(core): draft-to-book conversion assistant`

---

### Task 15: Volltextsuche (AI-Projekt)

**Files:**
- Create: `src/NoteEvolution.AI/Search/ISearchService.cs`, `FullTextSearchService.cs`, `FtsQuery.cs`
- Test: `tests/NoteEvolution.AI.Tests/Search/FullTextSearchTests.cs`, `FtsQueryTests.cs`

**Interfaces:**
- Consumes: `INoteRepository`, `NoteBlock`, `NoteFilter` (Task 6).
- Produces:
  - `record SearchQuery(string Text, NoteFilter Filter, int Limit = 100)`; `record SearchHit(Guid NoteBlockKey, double Score)`
  - `interface ISearchService { void Rebuild(INoteRepository notes); void UpdatePage(INoteRepository notes, string pagePath); IReadOnlyList<SearchHit> Search(SearchQuery query); }`
  - `sealed class FullTextSearchService : ISearchService, IDisposable` mit SQLite im Speicher (`Data Source=:memory:`). Tabellen `CREATE VIRTUAL TABLE notes USING fts5(content, context, key UNINDEXED, page UNINDEXED, tokenize='unicode61 remove_diacritics 2')` und `meta(key TEXT PRIMARY KEY, page TEXT, date TEXT, is_used INTEGER)`. Rangfolge `bm25(notes, 1.0, 0.3)`, Filter per Join auf `meta`. Indiziert wird jeder Notizblock mit dem eigenen `Content`; `context` ist `ContextPath` verbunden mit `" / "`.
  - `static class FtsQuery { string? Build(string userText); }`: In Wörter zerlegen, `"` entfernen, jedes Wort wird zu `"wort"*`, die Wörter werden mit Leerzeichen verbunden. Bleibt nichts übrig, ist das Ergebnis `null`, und `Search` liefert eine leere Liste.

- [ ] **Step 1: Failing tests schreiben**

```csharp
[Theory]
[InlineData("foo \"bar", "\"foo\"* \"bar\"*")]
[InlineData("AND OR NEAR", "\"AND\"* \"OR\"* \"NEAR\"*")]
[InlineData("-x (y) *", "\"-x\"* \"(y)\"*")]
[InlineData("   ", null)]
public void Build_EscapesUserInput(string input, string? expected) => Assert.Equal(expected, FtsQuery.Build(input)); // Review Focus 3
[Fact] public void Search_FindsByPrefixAndUmlautInsensitive() { /* "vertr" findet "Vertrauen"; "angst" findet "Ängste" */ }
[Fact] public void Search_HideUsed_ExcludesUsedAndDescendantsOfUsed() { }
[Fact] public void Search_DateRange_ExcludesUndatedPages() { }
[Fact] public void UpdatePage_ReflectsNewUsedInWithoutRebuild() { }
[Fact] public void Search_SpecialCharacters_DoNotThrow() { }
```

- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.AI.Tests`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(ai): FTS5 full-text note search with filters`

---

### Task 16: Einfaches PDF

**Files:**
- Create: `src/NoteEvolution.Pdf/IPdfExporter.cs`, `QuestPdfExporter.cs`, `ExportText.cs`, `Fonts/EBGaramond-{Regular,Italic,Bold,BoldItalic}.ttf`, `Fonts/OFL.txt` (Quelle: github.com/octaviopardo/EBGaramond12, Ordner `fonts/ttf`, als `EmbeddedResource`)
- Test: `tests/NoteEvolution.Pdf.Tests/PdfExportTests.cs`, `ExportTextTests.cs`

**Interfaces:**
- Consumes: `Book`, `OutlineNode`, `TextBlock`, `Paragraph`, `InlineMarkdown`.
- Produces:
  - `enum PdfPageSize { A4, A5 }`; `record PdfOptions(PdfPageSize Size)`; `record PdfExportReport(IReadOnlyList<string> Warnings)`
  - `interface IPdfExporter { PdfExportReport Export(Book book, Guid? scopeKey, PdfOptions options, Stream output); }`
  - `static class ExportText { string Clean(string content); }`: `[[x]]` → `x`, `#[[x]]` und `#tag` entfallen, `((uuid))` entfällt, doppelte Leerzeichen werden zu einem.
- Satzwerte: `QuestPDF.Settings.License = LicenseType.Community`. Schrift EB Garamond, Grundtext 11 pt, Zeilenhöhe 1,4. Ränder A4 25 mm, A5 18 mm. Überschriften Ebene 1: 20 pt, beginnt auf neuer Seite; Ebene 2: 16 pt; ab Ebene 3: 13 pt. Titelseite mit `Title` (28 pt) und `Dedication` (kursiv), ohne Seitenzahl. Danach das Inhaltsverzeichnis mit `Section`/`SectionLink` und `BeginPageNumberOfSection` sowie Einrückung nach Ebene. Seitenzahlen unten mittig. Absätze: `Text`, dann jeder Absatz ohne `IsNote` als eigener Absatz. Bilder `![alt](pfad)` werden relativ zum Ordner der Buchdatei aufgelöst und mit Seitenbreite eingebettet. Fehlt die Datei, erscheint der Platzhaltertext `[Bild fehlt: pfad]` und eine Warnung mit dem Pfad. Bei gesetztem `scopeKey` wird nur dieser Knoten mit seiner Überschrift exportiert; die Titelseite bleibt.

- [ ] **Step 1: Failing tests** (Auslesen mit PdfPig):

```csharp
[Fact] public void Export_ContainsHeadingsInOrder_NoNotesNoProperties() {
    var text = ExportAndRead(PdfSamples.SpecBookWithTwoChapters); // eigene Beispieldaten in tests/NoteEvolution.Pdf.Tests/PdfSamples.cs
    Assert.True(text.IndexOf("Liebe und Wahrheit") < text.IndexOf("Vertrauen"));
    Assert.DoesNotContain("noch ein Beispiel ergänzen", text);
    Assert.DoesNotContain("id::", text); Assert.DoesNotContain("source::", text);
}
[Fact] public void Export_TocPageNumberMatchesChapterPage() { }
[Fact] public void Export_MissingImage_PlaceholderAndWarning() { }
[Fact] public void Export_Scope_OnlySelectedChapter() { }
[Fact] public void Export_A5_PageSize() { /* 148 × 210 mm ± 1 pt */ }
[Fact] public void Clean_RemovesLinksTagsRefs() { }
```

- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.Pdf.Tests`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(pdf): simple book PDF with TOC, images and EB Garamond`

---

### Task 17: Desktop-Hülle, Sitzung und Grundlayout

**Files:**
- Create: `src/NoteEvolution.UI/Platform/IPlatformServices.cs`, `State/VaultSession.cs`, `State/AppState.cs`, `State/UiSettings.cs`, `Resources/Strings.resx`, `Components/Shell.razor`, `Components/HeaderBar.razor`, `Components/Splitter.razor`, `wwwroot/css/app.css`
- Create: `src/NoteEvolution.Desktop/Program.cs`, `PhotinoPlatformServices.cs`, `wwwroot/index.html`
- Test: `tests/NoteEvolution.UI.Tests/ShellTests.cs`, `UiSettingsTests.cs`

**Interfaces:**
- Consumes: alle Core-Dienste, `ISearchService`, `IPdfExporter`.
- Produces:
  - `interface IPlatformServices { Task<string?> PickFolderAsync(); Task<string?> PickSaveFileAsync(string suggestedName); void OpenExternal(string uri); string UserDataDirectory { get; } }`
  - `sealed class VaultSession : IDisposable { IVault Vault; INoteRepository Notes; IPageWriter Writer; ILinkService Links; UndoManager Undo; ISearchService Search; LinkChecker Checker; ExternalChangeHandler Changes; VaultWatcher Watcher; static Task<VaultSession> OpenAsync(string root, IClock clock); }`. Öffnen nach Spec 5.1 im Hintergrund: Vault parsen → Watcher starten → `RetryPending` → `Checker.Analyze` (ein nicht leerer Bericht öffnet den Dialog aus Task 21) → `Search.Rebuild`. Außerdem `BackupService.Cleanup`.
  - `sealed class AppState { VaultSession? Session; Book? CurrentBook; Guid CurrentSectionKey; ViewMode Mode; Guid? CursorTextBlockKey; Guid? FocusedNoteKey; event Action? Changed; void Notify(); }`; `enum ViewMode { Section, Manuscript }`
  - Nicht parsebare Dateien (`Page.IsReadOnly`): Die Kopfleiste zeigt ein Warnsymbol. Es öffnet eine Liste mit Dateiname und `ParseError` (Zeile).
  - `sealed class UiSettings { string? LastVault; double OutlineWidth = 260; double NotesWidth = 380; bool OutlineCollapsed; bool NotesCollapsed; ThemeChoice Theme = ThemeChoice.System; int FontSizePt = 12; int LineWidthCh = 70; bool HideUsed; bool LogseqHintShown; static UiSettings Load(string dir); void Save(string dir); }`. Datei `ui.json` im `UserDataDirectory` (`%APPDATA%/NoteEvolution` bzw. `~/.config/NoteEvolution`).
  - Kopfleiste: Buchauswahl (`Vault.Books`), Umschalter Abschnitt/Manuskript, PDF-Export, Verknüpfungsprüfung, Einstellungen, Rückgängig (`UndoManager.NextDescription` als Tooltip) und KI-Anzeige mit festem Text „KI: aus“ in Stufe 1. Beim ersten Öffnen erscheint einmalig der Hinweis, dasselbe Buch nicht gleichzeitig in Logseq zu bearbeiten.
  - Logging: Serilog-File-Sink mit `RollingInterval.Day` unter `<vault>/.noteevolution/logs/noteevolution-.log`; vor dem Öffnen eines Vaults unter `UserDataDirectory/logs`.
  - Design: CSS-Variablen für hell und dunkel, `ThemeChoice.System` folgt `prefers-color-scheme`.

- [ ] **Step 1: Failing bUnit-Tests:** `Shell_RendersThreeAreas_WithSavedWidths`, `HeaderBar_ShowsBooks_SelectionSetsCurrentBook`, `HeaderBar_ModeToggle_SwitchesViewMode`, `UiSettings_RoundTrip`.
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.UI.Tests`: FAIL → implementieren → PASS.
- [ ] **Step 5: Manuelle Prüfung:** `dotnet run --project src/NoteEvolution.Desktop`. Erwartet: Das Fenster öffnet, nach der Ordnerauswahl eines Test-Vaults erscheinen die Bücher in der Kopfleiste, die Trennlinien lassen sich ziehen, und die Breiten sind nach einem Neustart erhalten.
- [ ] **Step 6: Commit** `feat(ui): desktop shell, session and layout`

---

### Task 18: Gliederung

**Files:**
- Create: `src/NoteEvolution.UI/Components/OutlinePane.razor`, `OutlineItem.razor`
- Test: `tests/NoteEvolution.UI.Tests/OutlinePaneTests.cs`

**Interfaces:**
- Consumes: `AppState`, `OutlineEditor` (Task 10), `IPageWriter`.
- Produces: Baum aus `Book.Root` mit Titel, `WordCount` und `SourceCount`. Ein Klick setzt `AppState.CurrentSectionKey`. Ein Doppelklick ruft die Inline-Umbenennung auf (Enter speichert, Esc bricht ab). Mit dem Knopf „+“ wird eine Unter-Überschrift angelegt. HTML5-Ziehen setzt `OutlineEditor.MoveSection` um, danach `Writer.Save`. `Book.Warnings` erscheinen als Symbol mit Tooltip am Knoten. Der Vorspann erscheint als Eintrag „Vorspann“, wenn er Textblöcke hat. Der Bereich lässt sich einklappen.

- [ ] **Step 1: Failing tests:** `Renders_TitlesWithCounts`, `Click_SetsCurrentSection`, `DoubleClickEnter_RenamesAndSaves`, `Warning_ShowsIcon`, `Drop_MovesSectionAndSaves`.
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.UI.Tests --filter "FullyQualifiedName~OutlinePane"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(ui): outline pane`

---

### Task 19: Notizbereich

**Files:**
- Create: `src/NoteEvolution.UI/Components/NotesPane.razor`, `SearchTab.razor`, `JournalTab.razor`, `NoteCard.razor`
- Test: `tests/NoteEvolution.UI.Tests/NoteCardTests.cs`, `NotesPaneTests.cs`

**Interfaces:**
- Consumes: `INoteRepository`, `ISearchService`, `ILinkService.Adopt`, `AppState`, `IPlatformServices.OpenExternal`.
- Produces:
  - Reiter *Suche* und *Journal* (Journal mit Datumsfeld zum Springen). Die Suche wird 300 ms nach der letzten Eingabe ausgelöst. Schalter „Verwendete ausblenden“ (gespeichert in `UiSettings.HideUsed`) und Von/Bis-Datumsfilter gelten für beide Reiter.
  - `NoteCard` zeigt Datum bzw. Seitenname, im Suchreiter die Trefferquote (`SearchHit.Score`, auf 0–100 % der besten Trefferzahl normiert), Kontextpfad, den Blocktext mit Unteranstrichen (nach drei Zeilen eingeklappt, „mehr“ klappt auf) und bei Verwendung ✓. Ein Klick auf ✓ setzt `CurrentBook`/`CurrentSectionKey` auf die Stelle des ersten `used-in`-Eintrags mit Blockverweis. Aktionen: „Übernehmen“ am Block und an jedem Unteranstrich mit Position `After(CursorTextBlockKey)`, ohne Cursor `SectionEnd(CurrentSectionKey)`. „In Logseq öffnen“ öffnet `logseq://graph/<Ordnername des Vaults>?block-id=<id>`, ohne `id::` `?page=<Seitenname>`. Die Karte lässt sich ziehen (`draggable`, `dataTransfer`-Typ `application/x-noteevolution-note`, Wert = `NoteBlock.Key`).
  - Ist eine Notiz bereits verwendet, fragt „Übernehmen“ nicht nach; die Karte zeigt danach zwei Verwendungen.
  - Nach jedem Übernehmen: `Search.UpdatePage` für die Notizseite und `AppState.Notify()`, damit der Editor neu lädt.

- [ ] **Step 1: Failing tests:** `NoteCard_UsedShowsCheck_ClickNavigates`, `NoteCard_AdoptChild_CallsAdoptWithChildKey`, `NoteCard_LongText_CollapsedAfterThreeLines`, `NoteCard_LogseqLink_UsesBlockIdOrPage`, `NotesPane_HideUsedFiltersBothTabs`, `NotesPane_NoCursor_AdoptsAtSectionEnd`, `NoteCard_NoGermanLiterals` (rendert mit einem Ersatz-Localizer, der Schlüssel statt Texten liefert).
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.UI.Tests --filter "FullyQualifiedName~NoteCard|FullyQualifiedName~NotesPane"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Commit** `feat(ui): notes pane with search, journal and note cards`

---

### Task 20: Editor (TipTap, Abschnitts- und Manuskriptansicht)

**Files:**
- Create: `src/NoteEvolution.UI/Editor/js/package.json`, `js/editor.js`, `js/build.mjs` (esbuild, Ausgabe `../../wwwroot/js/editor.bundle.js`, Bundle wird eingecheckt)
- Create: `src/NoteEvolution.UI/Editor/IEditorInterop.cs`, `TipTapInterop.cs`, `EditorDocMapper.cs`, `src/NoteEvolution.UI/Components/EditorPane.razor`
- Test: `tests/NoteEvolution.UI.Tests/EditorDocMapperTests.cs`, `EditorPaneTests.cs` (mit `FakeEditorInterop`)

**Interfaces:**
- Consumes: `BookSnapshot`, `BookSync`, `ILinkService` (`ApplySyncEffects`, `RemoveSource`, `Adopt`), `IPageWriter`, `AppState`.
- Produces:
  - Schema (JS): `doc → (heading | textBlock)+`; `heading{key, level}` mit Inhalt `text*` (nur Manuskript); `textBlock{key, splitFrom, sources}` mit Inhalt `para+`; `para{key, depth, isNote}` mit Inhalt `inline*`. Erlaubte Marks: `bold`, `italic`; Knoten `hardBreak`. Der erste `para` hat `depth 0` (Blocktext), weitere Absätze haben `depth ≥ 1` (Unteranstriche).
  - JS-Plugin `uniqueKeys` (`appendTransaction`): Fehlt ein Schlüssel oder kommt er doppelt vor, bekommt der Knoten `crypto.randomUUID()`. Ein doppelter `textBlock` bekommt außerdem `splitFrom` = den ursprünglichen Schlüssel.
  - Tasten: `Enter` erzeugt einen neuen `para` mit `depth = max(1, aktuelle Tiefe)`. `Strg+Enter` teilt den `textBlock` am Cursor; der neue Teil erhält einen neuen Schlüssel, dazu `splitFrom` und `sources`. Am Blockende entsteht ein leerer Block ohne `splitFrom`. `Tab`/`Umschalt+Tab` ändern `depth` um ±1 im Bereich 1 … vorherige Tiefe + 1. `Strg+Umschalt+N` schaltet `isNote` um (nicht bei `depth 0`). `Strg+B`/`Strg+I` wie üblich. Markdown-Eingaberegeln für Überschriften sind abgeschaltet.
  - Darstellung über die CSS-Klasse am Editor: `mode-section` (abgesetzte Blöcke, Quellen-Chips unter dem Block; ein Klick auf einen Chip ruft `OnChipClicked`, × ruft `OnChipRemoved`; defekte Chips rot), `mode-manuscript` (nur Abstand, Randmarke bei Blöcken mit Quellen, Chips per Schalter). `isNote`-Absätze sind kursiv und gedämpft. Serifenschrift, `--font-size` und `--line-width` aus `UiSettings`.
  - `interface IEditorCallbacks { Task OnDocumentChanged(string docJson); Task OnCursorBlockChanged(Guid? textBlockKey); Task OnChipClicked(Guid noteId); Task OnChipRemoved(Guid textBlockKey, Guid noteId); Task OnNoteDropped(Guid noteBlockKey, Guid? afterTextBlockKey); }`
  - `interface IEditorInterop : IAsyncDisposable { Task InitAsync(ElementReference host, IEditorCallbacks callbacks); Task SetDocumentAsync(string docJson, bool manuscript, bool showChips); }`
  - `static class EditorDocMapper { string ToJson(SectionSnapshot snapshot); SectionSnapshot FromJson(string json, Guid scopeKey, bool includeSubsections); }`. Inline-Runs werden über `InlineMarkdown`, `hardBreak` als `"\n"` abgebildet.
  - `EditorPane`: lädt den Snapshot bei einem Wechsel von Buch, Abschnitt oder Ansicht und nach Strukturänderungen über `AppState.Changed`. `OnDocumentChanged` wird 1000 ms entprellt und läuft dann so: `FromJson` → `BookSync.Apply` → wenn `Changed`: `Writer.Save(book.Page)`, `Links.ApplySyncEffects`, `Book.Load` neu, Gliederung benachrichtigen. Vor jeder Strukturoperation (Übernehmen, Chip entfernen, Rückgängig, Abschnittswechsel) läuft `FlushAsync()`, damit ausstehende Eingaben zuerst gespeichert werden. `OnNoteDropped` → `Adopt` mit `After(afterKey)` bzw. `SectionStart(CurrentSectionKey)`. `OnChipClicked` → Notizbereich zeigt die Notiz (`AppState.FocusedNoteKey`). Eine schreibgeschützte Buchseite zeigt den Editor nicht editierbar mit Hinweis.

- [ ] **Step 1: Failing tests schreiben**

```csharp
[Fact] public void Mapper_RoundTrip_SpecExample() { /* Create → ToJson → FromJson == Snapshot */ }
[Fact] public void Mapper_BoldItalicAndHardBreak() { }
[Fact] public async Task Pane_EditThenDebounce_SavesOnceAfter1000ms() { /* Fake-Uhr bzw. TaskCompletion; zwei Änderungen innerhalb 1 s → ein Save */ }
[Fact] public async Task Pane_DropNote_AdoptsAfterBlock_ReloadsDocument() { }
[Fact] public async Task Pane_ChipRemove_FlushesThenRemovesSource() { }
[Fact] public async Task Pane_SplitFromJs_AddsUsageToNote() { }
```

- [ ] **Step 2–4:** `cd src/NoteEvolution.UI/Editor/js && npm install && node build.mjs`, danach `dotnet test tests/NoteEvolution.UI.Tests --filter "FullyQualifiedName~EditorDocMapper|FullyQualifiedName~EditorPane"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Manuelle Prüfung** im Desktop-Programm mit einem Test-Vault:
  1. Text tippen: Nach etwa 1 s ändert sich nur diese Zeile in der Datei (`git diff --no-index` gegen die Kopie).
  2. `Strg+Enter` in der Blockmitte: Die Notiz hat danach zwei `used-in`-Einträge.
  3. `Tab`, `Umschalt+Tab` und `Strg+Umschalt+N` wirken wie beschrieben.
  4. Eine Notiz ins Buch ziehen: Der Block erscheint an der Ablagestelle.
  5. In der Manuskriptansicht einen Block unter ein anderes Kapitel ziehen: Er ist danach in der Datei richtig eingerückt.
- [ ] **Step 6: Commit** `feat(ui): TipTap editor with section and manuscript views`

---

### Task 21: Dialoge, Assistenten-Oberfläche, PDF-Export, Einstellungen

**Files:**
- Create: `src/NoteEvolution.UI/Components/Dialogs/ConflictDialog.razor`, `LinkCheckDialog.razor`, `HandledWizard.razor`, `DraftWizard.razor`, `PdfExportDialog.razor`, `SettingsDialog.razor`, `src/NoteEvolution.UI/Components/UndoToast.razor`
- Test: `tests/NoteEvolution.UI.Tests/DialogTests.cs`

**Interfaces:**
- Consumes: `ExternalChangeHandler`, `LinkChecker`, `HandledConverter`, `DraftConverter`, `IPdfExporter`, `VaultSettings`, `UiSettings`, `UndoManager`.
- Produces:
  - `ConflictDialog`: zeigt pro `BlockConflict` die lokale und die externe Fassung nebeneinander, mit den Optionen „meine Fassung“, „externe Fassung“ und „beide nebeneinander“. Nach dem Bestätigen folgen `Changes.Resolve` und das Speichern. Ausgelöst wird der Dialog durch `VaultWatcher.ExternalChange` → `Handle`, wenn das Ergebnis `Conflict` ist. `Reloaded` lädt Editor und Suche still neu.
  - `LinkCheckDialog`: „N fehlende Verweise ergänzt“, eine Liste der Waisen mit je zwei Knöpfen (`MarkUnknown`/`Remove`) und eine Liste der defekten Quellen (nur zur Information, ein Klick springt zum Block).
  - `HandledWizard`: Liste mit Kontrollkästchen (alle vorausgewählt), Buchauswahl, „Umwandeln“ mit Bestätigung, danach `Search.UpdatePage`.
  - `DraftWizard`: Seitenauswahl (nur Nicht-Bücher), Baumvorschau mit Umschalter Überschrift/Textblock pro Knoten und der berechneten Ebene, „Umwandeln“ mit Bestätigung. Danach erscheint das Buch in der Buchauswahl.
  - `PdfExportDialog`: Umfang (ganzes Buch oder aktueller Abschnitt mit Teilbaum), A4/A5, Speicherort über `IPlatformServices.PickSaveFileAsync`, danach die Warnliste.
  - `SettingsDialog`: Notizordner (`VaultSettings.NoteFolders`, ändern löst ein Neuladen aus), Design, Schriftgröße (10–20 pt), Zeilenbreite (50–100 Zeichen).
  - `UndoToast`: erscheint nach Übernehmen und Löschen 8 s lang mit „Rückgängig“.

- [ ] **Step 1: Failing tests:** `ConflictDialog_ChoiceBoth_CallsResolveWithBoth`, `LinkCheckDialog_MarkUnknown_CallsResolve`, `HandledWizard_DeselectedItemsNotConverted`, `DraftWizard_ToggleUpdatesLevels`, `PdfExportDialog_ShowsWarningsAfterExport`, `SettingsDialog_FontSizeClampedAndSaved`, `UndoToast_ClickUndo_CallsUndoManager`.
- [ ] **Step 2–4:** `dotnet test tests/NoteEvolution.UI.Tests --filter "FullyQualifiedName~DialogTests"`: FAIL → implementieren → PASS.
- [ ] **Step 5: Gesamtlauf:** `dotnet test NoteEvolution.slnx` → alle Tests grün, `dotnet build NoteEvolution.slnx -c Release` → 0 Warnungen.
- [ ] **Step 6: Manuelle Abnahme** gegen die Erfolgskriterien aus Spec 1 mit einem künstlichen Test-Vault:
  1. Übernehmen mit einem Handgriff, danach ✓ an der Karte und ein Chip am Block.
  2. „Verwendete ausblenden“ wirkt.
  3. Der Vault öffnet in Logseq ohne Fehler; `git diff` zeigt nur die erwarteten Zeilen.
  4. Das PDF öffnet sich mit Inhaltsverzeichnis.
- [ ] **Step 7: Commit** `feat(ui): conflict, link check, assistants, PDF export and settings dialogs`
