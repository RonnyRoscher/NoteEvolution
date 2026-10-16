#:project ../../src/NoteEvolution.Core/NoteEvolution.Core.csproj
#:project ../../src/NoteEvolution.AI/NoteEvolution.AI.csproj
#:property PublishAot=false

// Quality and speed measurement of the language model's topic understanding (spec 9.2, 9.3). Not part of the test suite:
// it needs a running Ollama with the model installed and the local embedding model of the app in the user profile.
//
//   dotnet run tests/quality/measure.cs -- --ollama http://127.0.0.1:11434 --model qwen2.5:7b [--embed ID] [--limit N] [--keep DIR]
//
// With --keep DIR the vault and its computed summaries stay in DIR; a later run with the same DIR reuses them and only
// computes what changed (a run then mainly measures "Genauer prüfen").
//
// It copies Referenzbuch.md and Notizen.md into a temporary vault, measures today's "Wohin damit?" (correct chapter among
// the first 3), computes all heading summaries and note core statements with the real model into a temporary store,
// measures the new two-stage search and "Genauer prüfen" (correct sub-chapter first), and prints the response times.
// Nothing outside the temporary vault (or DIR) is written; no model is pulled.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Language;
using NoteEvolution.AI.Model;
using NoteEvolution.AI.Relevance;
using NoteEvolution.AI.Semantic;
using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Vaults;

var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("Aufruf / usage: dotnet run tests/quality/measure.cs -- [--ollama URL] [--model NAME] [--embed MODEL_ID] [--limit N] [--keep DIR]");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var dataDir = Paths.DataDirectory();
var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoteEvolution");
var models = new ModelStore(userData);
var embedId = options.Embed ?? Paths.SavedModelId(userData) ?? ModelCatalog.Default.Id;
if (ModelCatalog.Find(embedId) is not { } embedModel)
{
    Console.Error.WriteLine($"Unbekanntes Einbettungsmodell / unknown embedding model: {embedId}");
    return 2;
}

if (!models.IsInstalled(embedModel))
{
    Console.Error.WriteLine($"Einbettungsmodell nicht installiert / embedding model not installed: {models.DirectoryOf(embedModel)}");
    return 2;
}

using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var chat = new TimedChat(new OllamaChatModel(http, options.Ollama));
if (await chat.VersionAsync(cts.Token) is not { } version)
{
    Console.Error.WriteLine($"Ollama nicht erreichbar / Ollama unreachable: {options.Ollama}");
    return 3;
}

var installed = await chat.ModelsAsync(cts.Token);
if (!installed.Any(m => string.Equals(m.Name, options.Model, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(m.Name, options.Model + ":latest", StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine($"Modell nicht installiert / model not installed: {options.Model} (ollama pull {options.Model})");
    return 3;
}

var root = options.Keep is { } keep ? Path.GetFullPath(keep) : Path.Combine(Path.GetTempPath(), "ne-quality-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(Path.Combine(root, "pages"));
    Directory.CreateDirectory(Path.Combine(root, "journals"));
    File.Copy(Path.Combine(dataDir, "Referenzbuch.md"), Path.Combine(root, "pages", "Referenzbuch.md"), overwrite: true);
    File.Copy(Path.Combine(dataDir, "Notizen.md"), Path.Combine(root, "journals", "2026_10_15.md"), overwrite: true);
    var expected = JsonSerializer.Deserialize<Dictionary<string, Expectation>>(
        File.ReadAllBytes(Path.Combine(dataDir, "expected.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    var vault = Vault.Open(root);
    var repository = new NoteRepository(vault);
    var book = vault.Books.Single();
    var cases = new List<Case>();
    foreach (var note in repository.All().Where(n => n.Block.Parent is null))
    {
        var firstLine = note.Block.Content.Split('\n', 2)[0].Trim();
        if (!expected.TryGetValue(firstLine, out var expectation))
        {
            Console.Error.WriteLine($"Keine Erwartung / no expectation: {firstLine}");
            return 2;
        }

        var chapter = book.Root.Children.Single(c => c.Title == expectation.Chapter);
        var subchapter = chapter.Children.Single(c => c.Title == expectation.Subchapter);
        cases.Add(new Case(note, EmbeddingText.ForNote(note), chapter, subchapter, expectation.OtherAspect, firstLine));
    }

    if (options.Limit is { } limit)
    {
        cases = [.. cases.Take(limit)];
    }

    Console.WriteLine("NoteEvolution – Qualitätsmessung / quality measurement (spec 9.2, 9.3)");
    Console.WriteLine($"Ollama {version} @ {options.Ollama} · Sprachmodell / language model: {options.Model}");
    Console.WriteLine($"Einbettungsmodell / embedding model: {embedModel.Id}");
    Console.WriteLine($"Notizen / notes: {cases.Count} (anderer Aspekt / other aspect: {cases.Count(c => c.OtherAspect)})");
    Console.WriteLine();

    using var embedder = OnnxEmbedder.Load(models, embedModel);
    var logger = new ConsoleLogger();
    using var index = new SemanticIndex(embedder, VectorCache.Open(root, embedModel.Id), embedModel.Id, logger);
    await index.RebuildAsync(repository, cts.Token);
    if (!index.IsReady)
    {
        Console.Error.WriteLine($"Einbettung fehlgeschlagen / embedding failed: {index.Status.Error}");
        return 4;
    }

    // (1) Today: the sections of "Wohin damit?", and the two-stage topics on today's section vectors (no understanding).
    var today = new RelevanceService(index);
    var todayWhereTo = cases.Select(c => today.WhereTo(c.Note.Key, c.Text, book, 3)
        .Any(hit => Within(book.FindNode(hit.SectionKey), c.Chapter))).ToList();
    var todayTopics = cases.Select(c => today.Topics(c.Note.Key, c.Text, book, 3)
        .Any(hit => Within(book.FindNode(hit.SectionKey), c.Chapter))).ToList();

    // (2) All summaries and core statements with the real model, then the new two-stage search.
    using var store = UnderstandingStore.Open(root);
    var bookItems = UnderstandingItems.ForBook(book, options.Model);
    var noteItems = cases.Select(c => UnderstandingItems.ForNote(c.Note, options.Model)).OfType<UnderstandingItem>().ToList();
    Console.WriteLine($"Berechne / computing {bookItems.Count} Zusammenfassungen / summaries, {noteItems.Count} Kernaussagen / core statements …");
    var computeClock = Stopwatch.StartNew();
    using (var worker = new UnderstandingWorker(chat, options.Model, store, text => embedder.Embed([text])[0], embedModel.Id,
               TimeProvider.System, () => false, logger))
    {
        worker.SetWork(bookItems, null, noteItems, []);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var run = worker.RunAsync(stop.Token);
        var shown = "";
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            var progress = worker.Progress;
            var line = $"  {progress.Done}/{progress.Total}";
            if (line != shown)
            {
                Console.WriteLine($"{line}  ({computeClock.Elapsed:hh\\:mm\\:ss})");
                shown = line;
            }

            if (progress.Pause is UnderstandingPause.Unreachable or UnderstandingPause.ModelMissing)
            {
                Console.Error.WriteLine($"Abbruch / aborted: {progress.Pause}");
                return 3;
            }

            if (progress.Total > 0 && progress.Done >= progress.Total)
            {
                break;
            }
        }

        stop.Cancel();
        await run;
    }

    var summaries = bookItems.Count(item => store.Get(item.Fingerprint) is { Vector: not null });
    var cores = noteItems.Count(item => store.Get(item.Fingerprint) is { Vector: not null });
    Console.WriteLine($"Fertig / done in {computeClock.Elapsed:hh\\:mm\\:ss}: {summaries}/{bookItems.Count} Zusammenfassungen / summaries, {cores}/{noteItems.Count} Kernaussagen / core statements");
    Console.WriteLine();

    var understanding = new StoreUnderstanding(store, options.Model, embedModel.Id);
    understanding.UpdateNotes(noteItems.ToDictionary(item => item.Key, item => item.Fingerprint));
    var relevance = new RelevanceService(index, understanding);
    var newTopics = cases.Select(c => relevance.Topics(c.Note.Key, c.Text, book, 3)
        .Any(hit => Within(book.FindNode(hit.SectionKey), c.Chapter))).ToList();

    // (3) "Genauer prüfen" as the session asks it (VaultSession.CheckPlacementAsync): best 3 topics, their places, the model's choice.
    var checks = new List<bool>();
    var verdicts = new List<PlacementVerdict?>();
    var unusable = 0;
    for (var i = 0; i < cases.Count; i++)
    {
        var c = cases[i];
        var topics = relevance.Topics(c.Note.Key, c.Text, book, PlacementCheck.TopicLimit);
        var places = relevance.Placements(c.Note.Key, c.Text, book, int.MaxValue)
            .GroupBy(hit => hit.SectionKey)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PlacementHit>)[.. group]);
        var prompt = PlacementCheck.Prompt(book, c.Text[EmbeddingText.PassagePrefix.Length..], topics, places,
            section => understanding.Heading(book, section)?.Text);
        PlacementVerdict? verdict = null;
        if (prompt.Topics.Count > 0)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(UnderstandingWorker.RequestTimeout);
            try
            {
                var answer = await chat.CheckAsync(options.Model, prompt.System, prompt.User,
                    PlacementCheck.Options(prompt), timeout.Token);
                verdict = PlacementCheck.Parse(answer, prompt);
                if (verdict is null)
                {
                    Console.Error.WriteLine($"  Unbrauchbare Antwort / unusable answer ({c.FirstLine[..Math.Min(40, c.FirstLine.Length)]}): {answer.ReplaceLineEndings(" ")}");
                }
            }
            catch (Exception ex) when (ex is ChatModelUnavailableException
                                       || (ex is OperationCanceledException && !cts.IsCancellationRequested))
            {
                Console.Error.WriteLine($"  Prüfung fehlgeschlagen / check failed: {ex.Message}");
            }
        }

        verdicts.Add(verdict);
        if (verdict is null)
        {
            unusable++;
        }

        var chosen = verdict is null ? null
            : verdict.AfterTextBlockKey is { } after ? book.FindTextBlock(after)?.Section
            : book.FindNode(verdict.SectionKey);
        checks.Add(Within(chosen, c.Subchapter));
        Console.WriteLine($"  Genauer prüfen / check {i + 1}/{cases.Count}");
    }

    Console.WriteLine();
    PrintRates(cases, todayWhereTo, todayTopics, newTopics, checks, unusable, verdicts, book);
    PrintTimes(chat);
    PrintMisses(cases, newTopics, checks);
    return 0;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("Abgebrochen / cancelled");
    return 130;
}
finally
{
    SqliteConnection.ClearAllPools();
    if (options.Keep is null)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            Console.Error.WriteLine($"Temporärer Vault bleibt / temporary vault left: {root}");
        }
    }
}

// Whether the section is the target or lies within it.
static bool Within(OutlineNode? section, OutlineNode target)
{
    for (var node = section; node is not null; node = node.Parent)
    {
        if (node.Key == target.Key)
        {
            return true;
        }
    }

    return false;
}

static void PrintRates(List<Case> cases, List<bool> todayWhereTo, List<bool> todayTopics, List<bool> newTopics,
    List<bool> checks, int unusable, List<PlacementVerdict?> verdicts, Book book)
{
    var aspect = cases.Select(c => c.OtherAspect).ToList();
    Console.WriteLine("Richtiges Kapitel unter den ersten 3 / correct chapter in the top 3");
    Console.WriteLine($"  {"",-58}{"alle / all",18}{"anderer Aspekt / other aspect",32}");
    Row("heute: Wohin damit? (Abschnitte) / today: sections", todayWhereTo);
    Row("heute zweistufig ohne Modell / two-stage, no model", todayTopics);
    Row("neu zweistufig mit Modell / new two-stage with model", newTopics);
    var deltaAll = Rate(newTopics, _ => true) - Rate(todayWhereTo, _ => true);
    var deltaAspect = Rate(newTopics, i => aspect[i]) - Rate(todayWhereTo, i => aspect[i]);
    Console.WriteLine($"  {"Differenz neu − heute / delta new − today",-58}{Points(deltaAll),18}{Points(deltaAspect),32}");
    Console.WriteLine();
    Console.WriteLine("Genauer prüfen: richtiges Unterkapitel an 1. Stelle / check: correct sub-chapter first");
    Row("Genauer prüfen / check", checks);
    Console.WriteLine($"  ohne verwertbare Antwort / no usable answer: {unusable}");
    Console.WriteLine();
    Console.WriteLine("Passung / fit");
    foreach (var (fit, label) in new[] { (PlacementFit.Good, "gut / good"), (PlacementFit.Partial, "teilweise / partial"), (PlacementFit.Poor, "kaum / poor") })
    {
        Row(label, checks, i => verdicts[i]?.Fit == fit);
    }

    Console.WriteLine();
    Console.WriteLine("Unsicher / uncertain");
    Row("Modell ≠ Embedding / model ≠ embedding", checks, i => verdicts[i] is { } v && PlacementCheck.IsUncertain(book, v));
    Console.WriteLine();
    // Without a usable verdict a goal cannot be judged: n/a, not "met".
    var withVerdict = verdicts.Count(v => v is not null);
    double? poorShare = withVerdict == 0 ? null : (double)verdicts.Count(v => v?.Fit == PlacementFit.Poor) / withVerdict;
    var goodIndices = Enumerable.Range(0, verdicts.Count).Where(i => verdicts[i]?.Fit == PlacementFit.Good).ToList();
    double? goodRate = goodIndices.Count == 0 ? null : (double)goodIndices.Count(i => checks[i]) / goodIndices.Count;
    double? firstRate = withVerdict == 0 ? null : Rate(checks, _ => true);
    Console.WriteLine("Ziele / goals (spec 1)");
    Console.WriteLine($"  richtiges Unterkapitel an 1. Stelle ≥ 65 % / correct sub-chapter first ≥ 65 %: {Goal(firstRate, r => r >= 0.65)}");
    Console.WriteLine($"  kaum ≤ 10 % / poor ≤ 10 %: {Goal(poorShare, r => r <= 0.10)}");
    Console.WriteLine($"  gut richtig ≥ 75 % / good correct ≥ 75 %: {Goal(goodRate, r => r >= 0.75)}");
    Console.WriteLine();
    var allOk = Rate(newTopics, _ => true) >= 0.8;
    var aspectOk = deltaAspect >= 0.2;
    Console.WriteLine("Erfolgskriterien / success criteria (spec 1)");
    Console.WriteLine($"  neu ≥ 80 % (alle) / new ≥ 80 % (all): {(allOk ? "erfüllt / met" : "nicht erfüllt / not met")}");
    Console.WriteLine($"  anderer Aspekt ≥ +20 Pp. / other aspect ≥ +20 pp: {(aspectOk ? "erfüllt / met" : "nicht erfüllt / not met")}");
    Console.WriteLine();

    static string Goal(double? rate, Func<double, bool> met) => rate is not { } r
        ? "n/a"
        : $"{r:P0} {(met(r) ? "erfüllt / met" : "nicht erfüllt / not met")}";

    void Row(string label, List<bool> hits, Func<int, bool>? only = null) =>
        Console.WriteLine($"  {label,-58}{Cell(hits, i => only?.Invoke(i) ?? true),18}{Cell(hits, i => (only?.Invoke(i) ?? true) && aspect[i]),32}");
}

static string Cell(List<bool> hits, Func<int, bool> include)
{
    var indices = Enumerable.Range(0, hits.Count).Where(include).ToList();
    var hit = indices.Count(i => hits[i]);
    return indices.Count == 0 ? "–" : $"{hit}/{indices.Count} = {Rate(hits, include) * 100:0} %";
}

static double Rate(List<bool> hits, Func<int, bool> include)
{
    var indices = Enumerable.Range(0, hits.Count).Where(include).ToList();
    return indices.Count == 0 ? 0 : (double)indices.Count(i => hits[i]) / indices.Count;
}

static string Points(double delta) => $"{delta * 100:+0;-0;0} Pp. / pp";

static void PrintTimes(TimedChat chat)
{
    Console.WriteLine("Antwortzeiten / response times (spec 9.3)");
    Console.WriteLine($"  {"",-34}{"Anzahl / count",16}{"Mittel / mean",16}{"Median",12}{"Max",12}");
    TimeRow("Zusammenfassung / summary", chat.Times(ChatKind.Summary));
    TimeRow("Kernaussage / core statement", chat.Times(ChatKind.Core));
    var checks = chat.Times(ChatKind.Check);
    TimeRow("Genauer prüfen / check", checks);
    if (checks.Count > 0 && Median(checks) > TimeSpan.FromMinutes(3))
    {
        Console.WriteLine("  Genauer prüfen dauert über 3 Minuten: qwen2.5:3b als Vorgabe vorschlagen (spec 9.3).");
        Console.WriteLine("  The check takes over 3 minutes: propose qwen2.5:3b as the default (spec 9.3).");
    }

    Console.WriteLine($"  fehlgeschlagene Anfragen / failed requests: {chat.Failures}");
    Console.WriteLine();

    static void TimeRow(string label, IReadOnlyList<TimeSpan> times) =>
        Console.WriteLine(times.Count == 0
            ? $"  {label,-34}{0,16}{"–",16}{"–",12}{"–",12}"
            : $"  {label,-34}{times.Count,16}{Seconds(TimeSpan.FromTicks((long)times.Average(t => t.Ticks))),16}{Seconds(Median(times)),12}{Seconds(times.Max()),12}");
}

static TimeSpan Median(IReadOnlyList<TimeSpan> times)
{
    var sorted = times.Order().ToList();
    return sorted.Count % 2 == 1
        ? sorted[sorted.Count / 2]
        : TimeSpan.FromTicks((sorted[sorted.Count / 2 - 1].Ticks + sorted[sorted.Count / 2].Ticks) / 2);
}

static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

static void PrintMisses(List<Case> cases, List<bool> newTopics, List<bool> checks)
{
    Console.WriteLine("Verfehlt (neu) / missed (new): K = Kapitel nicht unter den ersten 3 / chapter not in top 3, U = Unterkapitel / sub-chapter");
    for (var i = 0; i < cases.Count; i++)
    {
        if (newTopics[i] && checks[i])
        {
            continue;
        }

        var c = cases[i];
        var flags = (newTopics[i] ? " " : "K") + (checks[i] ? " " : "U") + (c.OtherAspect ? " A" : "  ");
        var line = c.FirstLine.Length > 70 ? c.FirstLine[..70] + "…" : c.FirstLine;
        Console.WriteLine($"  {flags}  {c.Chapter.Title} › {c.Subchapter.Title}: {line}");
    }
}

/// <summary>One reference note with its expected chapter and sub-chapter.</summary>
sealed record Case(NoteBlock Note, string Text, OutlineNode Chapter, OutlineNode Subchapter, bool OtherAspect, string FirstLine);

/// <summary>An entry of <c>expected.json</c> (keyed by the note's first line).</summary>
sealed record Expectation(string Chapter, string Subchapter, bool OtherAspect);

/// <summary>The command line: <c>--ollama URL</c>, <c>--model NAME</c>, <c>--embed MODEL_ID</c>, <c>--limit N</c>, <c>--keep DIR</c>.</summary>
sealed record Options(Uri Ollama, string Model, string? Embed, int? Limit, string? Keep)
{
    public static Options? Parse(string[] args)
    {
        var ollama = new Uri("http://127.0.0.1:11434");
        var model = "qwen2.5:7b";
        string? embed = null;
        int? limit = null;
        string? keep = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
            {
                return null;
            }

            var value = args[++i];
            switch (args[i - 1])
            {
                case "--ollama" when Uri.TryCreate(value, UriKind.Absolute, out var uri):
                    ollama = uri;
                    break;
                case "--model":
                    model = value;
                    break;
                case "--embed":
                    embed = value;
                    break;
                case "--limit" when int.TryParse(value, out var n) && n > 0:
                    limit = n;
                    break;
                case "--keep":
                    keep = value;
                    break;
                default:
                    return null;
            }
        }

        return new Options(ollama, model, embed, limit, keep);
    }
}

static class Paths
{
    /// <summary>The folder of this program, which holds the reference data.</summary>
    public static string DataDirectory([CallerFilePath] string source = "") =>
        AppContext.GetData("EntryPointFileDirectoryPath") is string { Length: > 0 } dir ? dir
        : File.Exists(source) ? Path.GetDirectoryName(source)!
        : Path.Combine(Environment.CurrentDirectory, "tests", "quality");

    /// <summary>The embedding model chosen in the app (<c>AiModelId</c> in <c>ui.json</c>), if any.</summary>
    public static string? SavedModelId(string userData)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(userData, "ui.json")));
            return json.RootElement.TryGetProperty("AiModelId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

enum ChatKind { Summary, Core, Check }

/// <summary>The chat model with every successful answer timed: background requests by their token limit, checks through <see cref="CheckAsync"/>.</summary>
sealed class TimedChat(IChatModel inner) : IChatModel
{
    private readonly Lock _gate = new();
    private readonly List<(ChatKind Kind, TimeSpan Time)> _times = [];
    private int _failures;

    public int Failures => Volatile.Read(ref _failures);

    public IReadOnlyList<TimeSpan> Times(ChatKind kind)
    {
        lock (_gate) return [.. _times.Where(t => t.Kind == kind).Select(t => t.Time)];
    }

    public Task<string?> VersionAsync(CancellationToken ct) => inner.VersionAsync(ct);

    public Task<IReadOnlyList<ChatModelInfo>> ModelsAsync(CancellationToken ct) => inner.ModelsAsync(ct);

    public Task<string> ChatAsync(string model, string system, string user, ChatOptions options, CancellationToken ct) =>
        Timed(options.MaxTokens == UnderstandingPrompts.CoreTokens ? ChatKind.Core : ChatKind.Summary, model, system, user, options, ct);

    public Task<string> CheckAsync(string model, string system, string user, ChatOptions options, CancellationToken ct) =>
        Timed(ChatKind.Check, model, system, user, options, ct);

    public Task PullAsync(string model, IProgress<PullProgress>? progress, CancellationToken ct) =>
        throw new NotSupportedException("The measurement never pulls a model.");

    private async Task<string> Timed(ChatKind kind, string model, string system, string user, ChatOptions options, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var answer = await inner.ChatAsync(model, system, user, options, ct);
            lock (_gate) _times.Add((kind, clock.Elapsed));
            return answer;
        }
        catch
        {
            Interlocked.Increment(ref _failures);
            throw;
        }
    }
}

/// <summary>Writes warnings and errors of the worker and the index to the error output.</summary>
sealed class ConsoleLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            Console.Error.WriteLine($"  [{logLevel}] {formatter(state, exception)}{(exception is null ? "" : ": " + exception.Message)}");
        }
    }
}
