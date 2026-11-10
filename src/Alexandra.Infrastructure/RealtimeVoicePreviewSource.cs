using System.Text.Json;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

public sealed class RealtimeVoicePreviewSource(Func<IRealtimeTransport> transportFactory) : IVoicePreviewSource
{
    public async Task<byte[]> GenerateAsync(string voice, string key, CancellationToken token)
    {
        if (!VoiceCatalog.IsValid(voice)) throw new ArgumentException("Unsupported preview voice.", nameof(voice));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A key is required.", nameof(key));
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        await using var transport = transportFactory();
        // Voices cannot change after audio is emitted: each uncached voice gets its own short-lived session.
        await transport.ConnectAsync(RealtimeProtocol.Model, key, ct);
        await transport.SendAsync(JsonSerializer.Serialize(SessionUpdate(voice)), ct);
        using var pcm = new MemoryStream();
        var requested = false;
        string? responseId = null;
        await foreach (var json in transport.ReceiveAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            switch (e.GetProperty("type").GetString())
            {
                case "session.updated" when !requested:
                    // Do not silently preview a server fallback voice.
                    if (e.GetProperty("session").GetProperty("audio").GetProperty("output").GetProperty("voice").GetString() != voice)
                        throw new InvalidOperationException("Preview voice was not confirmed.");
                    requested = true;
                    await transport.SendAsync(JsonSerializer.Serialize(CreateResponse()), ct);
                    break;
                case "response.created" when requested:
                    if (responseId is not null) throw new InvalidOperationException("Unexpected additional preview response.");
                    responseId = e.GetProperty("response").GetProperty("id").GetString();
                    break;
                case "response.output_audio.delta" when requested:
                    if (responseId is null || e.GetProperty("response_id").GetString() != responseId)
                        throw new InvalidOperationException("Unexpected preview audio.");
                    var delta = e.GetProperty("delta").GetString() ?? "";
                    if (delta.Length > VoiceCatalog.MaxPcmBytes * 2) throw new InvalidOperationException("Preview too long.");
                    var bytes = Convert.FromBase64String(delta);
                    if (bytes.Length % 2 != 0 || pcm.Length + bytes.Length > VoiceCatalog.MaxPcmBytes)
                        throw new InvalidOperationException("Invalid preview audio.");
                    pcm.Write(bytes);
                    break;
                case "response.done" when requested:
                    var response = e.GetProperty("response");
                    if (responseId is null || response.GetProperty("id").GetString() != responseId ||
                        response.GetProperty("status").GetString() != "completed" || pcm.Length == 0 ||
                        response.GetProperty("output").EnumerateArray().Any(item => item.GetProperty("type").GetString() != "message"))
                        throw new InvalidOperationException("Preview did not complete.");
                    return pcm.ToArray();
                case "error":
                    // Provider messages can echo inputs; never show/log raw error payloads or credentials.
                    throw new InvalidOperationException("Voice preview service error.");
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Voice preview connection ended early.");
    }
    public static object SessionUpdate(string voice)
    {
        if (!VoiceCatalog.IsValid(voice)) throw new ArgumentException("Unsupported preview voice.", nameof(voice));
        return new {
            type = "session.update",
            session = new {
                type = "realtime", output_modalities = new[] { "audio" },
                instructions = "You provide a short AI voice sample. Speak the supplied sentence exactly once, naturally and warmly. Do not add an introduction or any other words.",
                max_output_tokens = 512, tools = Array.Empty<object>(), tool_choice = "none",
                audio = new {
                    input = new { turn_detection = (object?)null },
                    output = new { voice, format = new { type = "audio/pcm", rate = 24000 } }
                }
            }
        };
    }
    public static object CreateResponse() => new {
        type = "response.create",
        response = new {
            conversation = "none", output_modalities = new[] { "audio" },
            max_output_tokens = 512, tools = Array.Empty<object>(), tool_choice = "none",
            input = new[] { new { type = "message", role = "user",
                content = new[] { new { type = "input_text", text = "Say exactly: " + VoiceCatalog.SampleLine } } } }
        }
    };
}
