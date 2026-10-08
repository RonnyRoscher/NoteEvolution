using Microsoft.ML.OnnxRuntime;
using NoteEvolution.AI.Model;

namespace NoteEvolution.AI.Embeddings;

/// <summary>
/// Embeds with a SentencePiece ONNX encoder model (e5, bge-m3) on the CPU: one padded batch per call, the model's role
/// prefixes, pooling of <c>last_hidden_state</c> as the model prescribes (mean over the attention mask, or the first
/// token), then L2 normalization. Safe to call from several threads: <c>InferenceSession.Run</c> is thread-safe.
/// <see cref="Dispose"/> terminates running inference (those calls end with <see cref="OperationCanceledException"/>) and
/// waits for it before releasing the native session; later calls throw <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private const string InputIds = "input_ids";
    private const string AttentionMask = "attention_mask";
    private const string TokenTypeIds = "token_type_ids";
    private const string LastHiddenState = "last_hidden_state";

    private readonly InferenceSession _session;
    private readonly E5Tokenizer _tokenizer;
    private readonly ModelInfo _model;
    private readonly int _maxTokens;
    private readonly bool _needsTokenTypeIds;

    // Guards the native session: Dispose must not free it while a Run is inside it (that is an access violation, not an exception).
    private readonly object _gate = new();
    private readonly CancellationTokenSource _disposing = new();
    private int _running;
    private bool _disposed;

    private OnnxEmbedder(InferenceSession session, E5Tokenizer tokenizer, ModelInfo model)
    {
        _session = session;
        _tokenizer = tokenizer;
        _model = model;
        _maxTokens = model.MaxTokens;
        Dimensions = model.Dimensions;

        var inputs = session.InputMetadata.Keys.ToHashSet();
        if (!inputs.Contains(InputIds) || !inputs.Contains(AttentionMask))
            throw new InvalidOperationException($"Model {model.Id} lacks the inputs {InputIds} and {AttentionMask} (has: {string.Join(", ", inputs)}).");
        _needsTokenTypeIds = inputs.Remove(TokenTypeIds);
        inputs.ExceptWith([InputIds, AttentionMask]);
        if (inputs.Count > 0)
            throw new InvalidOperationException($"Model {model.Id} has unsupported inputs: {string.Join(", ", inputs)}.");

        if (!session.OutputMetadata.TryGetValue(LastHiddenState, out var output) || output.Dimensions[^1] != model.Dimensions)
            throw new InvalidOperationException($"Model {model.Id} has no output {LastHiddenState} with {model.Dimensions} dimensions.");
    }

    public int Dimensions { get; }

    public static OnnxEmbedder Load(ModelStore store, ModelInfo model) => Load(store.DirectoryOf(model), model);

    /// <summary>Loads from an explicit model directory (tests point this at <c>NOTEEVOLUTION_MODEL_DIR</c>).</summary>
    internal static OnnxEmbedder Load(string modelDirectory, ModelInfo model)
    {
        string PathEndingIn(string extension) => Path.Combine(modelDirectory,
            model.Files.Single(f => f.RelativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .RelativePath.Replace('/', Path.DirectorySeparatorChar));

        var tokenizer = E5Tokenizer.Load(PathEndingIn(".model"));
        // Half the cores, so background indexing leaves the machine responsive.
        using var options = new SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
        var session = new InferenceSession(PathEndingIn(".onnx"), options);
        try
        {
            return new OnnxEmbedder(session, tokenizer, model);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var result = new float[texts.Count][];
        var encoded = new List<(int Index, long[] Ids)>();
        for (var i = 0; i < texts.Count; i++)
        {
            var text = EmbeddingText.ForModel(texts[i], _model);
            if (string.IsNullOrWhiteSpace(text)) result[i] = new float[Dimensions];
            else encoded.Add((i, _tokenizer.Encode(text, _maxTokens)));
        }

        if (encoded.Count > 0) EmbedBatch(encoded, result, ct);
        return result;
    }

    private void EmbedBatch(List<(int Index, long[] Ids)> encoded, float[][] result, CancellationToken ct)
    {
        var batch = encoded.Count;
        var tokens = encoded.Max(e => e.Ids.Length);
        var ids = new long[batch * tokens];
        var mask = new long[batch * tokens];
        Array.Fill(ids, E5Tokenizer.Padding);
        for (var b = 0; b < batch; b++)
        {
            encoded[b].Ids.CopyTo(ids, b * tokens);
            Array.Fill(mask, 1L, b * tokens, encoded[b].Ids.Length);
        }

        long[] shape = [batch, tokens];
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var typesValue = _needsTokenTypeIds ? OrtValue.CreateTensorValueFromMemory(new long[batch * tokens], shape) : null;
        List<string> names = [InputIds, AttentionMask];
        List<OrtValue> values = [idsValue, maskValue];
        if (typesValue is not null)
        {
            names.Add(TokenTypeIds);
            values.Add(typesValue);
        }

        EnterRun();
        try
        {
            using var runOptions = new RunOptions();
            using var cancel = ct.Register(() => runOptions.Terminate = true);
            using var stop = _disposing.Token.Register(() => runOptions.Terminate = true);
            IDisposableReadOnlyCollection<OrtValue> outputs;
            try
            {
                outputs = _session.Run(runOptions, names, values, [LastHiddenState]);
            }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (OnnxRuntimeException) when (_disposing.IsCancellationRequested)
            {
                throw new OperationCanceledException("The embedder was disposed during inference.", _disposing.Token);
            }

            using (outputs)
            {
                var hidden = outputs[0].GetTensorDataAsSpan<float>();
                var sequence = tokens * Dimensions;
                if (hidden.Length != batch * sequence)
                    throw new InvalidOperationException($"{LastHiddenState} has {hidden.Length} values, expected {batch}×{tokens}×{Dimensions}.");
                for (var b = 0; b < batch; b++)
                {
                    var states = hidden.Slice(b * sequence, sequence);
                    result[encoded[b].Index] = _model.Pooling == Pooling.Cls
                        ? VectorMath.ClsNormalize(states, tokens, Dimensions)
                        : VectorMath.MeanPoolNormalize(states, tokens, Dimensions, mask.AsSpan(b * tokens, tokens));
                }
            }
        }
        finally
        {
            ExitRun();
        }
    }

    /// <summary>Registers a run; a call that was overtaken by <see cref="Dispose"/> after it started ends as cancelled.</summary>
    private void EnterRun()
    {
        lock (_gate)
        {
            if (_disposed) throw new OperationCanceledException("The embedder was disposed during the call.");
            _running++;
        }
    }

    private void ExitRun()
    {
        lock (_gate)
        {
            if (--_running == 0) Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Terminates running inference, waits until it has left the session, then frees it. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _disposing.Cancel();
            while (_running > 0) Monitor.Wait(_gate);
        }

        _session.Dispose();
        _disposing.Dispose();
    }
}
