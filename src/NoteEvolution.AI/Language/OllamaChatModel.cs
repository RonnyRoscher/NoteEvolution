using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoteEvolution.AI.Language;

/// <summary><see cref="IChatModel"/> over the Ollama REST API at <paramref name="baseAddress"/>.</summary>
public sealed class OllamaChatModel(HttpClient http, Uri baseAddress) : IChatModel
{
    private sealed record VersionDto([property: JsonPropertyName("version")] string? Version);

    private sealed record TagsDto([property: JsonPropertyName("models")] List<TagDto>? Models);

    private sealed record TagDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("size")] long Size);

    private sealed record MessageDto(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content);

    private sealed record ChatOptionsDto(
        [property: JsonPropertyName("num_predict")] int NumPredict,
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record ChatRequestDto(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] MessageDto[] Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] ChatOptionsDto Options,
        [property: JsonPropertyName("format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format);

    private sealed record ChatResponseDto([property: JsonPropertyName("message")] MessageDto? Message);

    private sealed record PullRequestDto(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record PullLineDto(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("total")] long Total,
        [property: JsonPropertyName("completed")] long Completed,
        [property: JsonPropertyName("error")] string? Error);

    private Uri Url(string path) => new(baseAddress, path);

    public async Task<string?> VersionAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(Url("api/version"), ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var dto = await response.Content.ReadFromJsonAsync<VersionDto>(ct);
            return dto?.Version;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout, not a caller cancellation: treat as unreachable.
            return null;
        }
    }

    public async Task<IReadOnlyList<ChatModelInfo>> ModelsAsync(CancellationToken ct)
    {
        var dto = await SendAsync(() => http.GetAsync(Url("api/tags"), ct), "Listing models", ct,
            r => r.Content.ReadFromJsonAsync<TagsDto>(ct));
        return [.. (dto?.Models ?? []).Where(m => !string.IsNullOrEmpty(m.Name)).Select(m => new ChatModelInfo(m.Name!, m.Size))];
    }

    public async Task<string> ChatAsync(string model, string system, string user, ChatOptions options, CancellationToken ct)
    {
        var request = new ChatRequestDto(
            model,
            [new MessageDto("system", system), new MessageDto("user", user)],
            Stream: false,
            new ChatOptionsDto(options.MaxTokens, options.Temperature),
            options.Json ? "json" : null);

        var dto = await SendAsync(() => http.PostAsJsonAsync(Url("api/chat"), request, ct), "Chat request", ct,
            r => r.Content.ReadFromJsonAsync<ChatResponseDto>(ct));
        return dto?.Message?.Content
            ?? throw new ChatModelUnavailableException("Chat request returned no message.");
    }

    public async Task PullAsync(string model, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Url("api/pull"))
            {
                Content = JsonContent.Create(new PullRequestDto(model, Stream: true)),
            };
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            EnsureSuccess(response, "Model download");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var dto = JsonSerializer.Deserialize<PullLineDto>(line);
                if (dto is null)
                    continue;
                if (!string.IsNullOrEmpty(dto.Error))
                    throw new ChatModelUnavailableException($"Model download failed: {dto.Error}");
                progress?.Report(new PullProgress(dto.Status ?? "", dto.Completed, dto.Total));
            }
        }
        catch (HttpRequestException ex)
        {
            throw new ChatModelUnavailableException($"Model download failed: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new ChatModelUnavailableException($"Model download returned unreadable data: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new ChatModelUnavailableException($"Model download failed: {ex.Message}", ex);
        }
    }

    /// <summary>Runs a request and maps transport failures, non-success statuses and unreadable bodies to <see cref="ChatModelUnavailableException"/>.</summary>
    private static async Task<T?> SendAsync<T>(Func<Task<HttpResponseMessage>> send, string what, CancellationToken ct,
        Func<HttpResponseMessage, Task<T?>> read)
    {
        try
        {
            using var response = await send();
            EnsureSuccess(response, what);
            return await read(response);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatModelUnavailableException($"{what} failed: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new ChatModelUnavailableException($"{what} returned unreadable data: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout, not a caller cancellation.
            throw new ChatModelUnavailableException($"{what} timed out.", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
            throw new ChatModelUnavailableException($"{what} failed: HTTP {(int)response.StatusCode}.");
    }
}
