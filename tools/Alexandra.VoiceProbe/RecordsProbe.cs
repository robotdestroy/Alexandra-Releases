using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;

internal static class RecordsProbe
{
    // Local-only check: counts/timing, never personal records or identifiers.
    public static async Task VerifyLocalAsync()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games", "Frontier Developments", "Elite Dangerous");
        var source = new OnDemandGameRecords(() => folder);
        foreach (var topic in Enum.GetValues<GameRecordTopic>())
        {
            var timer = Stopwatch.StartNew();
            var result = await source.LookupAsync(topic, default);
            using var json = JsonDocument.Parse(result.Json);
            var available = json.RootElement.TryGetProperty("data", out var data);
            Console.WriteLine($"Local {topic}: readable={available}, durationMs={timer.ElapsedMilliseconds}, outputCharacters={result.Json.Length}");
            if (!available) throw new InvalidOperationException("Local lookup unavailable. No private details printed.");
            if (topic == GameRecordTopic.Carrier)
                Console.WriteLine($"Carrier identity found={data.GetProperty("identified").GetBoolean()}");
            if (topic == GameRecordTopic.Modules)
                Console.WriteLine($"Module storage found={data.GetProperty("storedModules").ValueKind != JsonValueKind.Null}; active loadout found={data.GetProperty("currentShipLoadout").ValueKind != JsonValueKind.Null}");
        }
    }
    // Explicit opt-in API evaluation. No real journals, microphone, playback, browser or clipboard.
    public static async Task RunAsync(string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "alexandra-records-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Journal.synthetic.log"), """
            {"timestamp":"2026-11-01T10:00:01Z","event":"LoadGame","Commander":"Synthetic Probe","FID":"SYNTHETIC","Ship":"mandalay","ShipID":1}
            {"timestamp":"2026-11-01T10:00:02Z","event":"Location","StarSystem":"Sol"}
            {"timestamp":"2026-11-01T10:00:03Z","event":"CarrierStats","CarrierID":700,"Callsign":"TST-123","Name":"Synthetic Horizon","FuelLevel":50}
            {"timestamp":"2026-11-01T10:00:04Z","event":"CarrierLocation","CarrierID":700,"StarSystem":"Sol"}
            {"timestamp":"2026-11-01T10:00:05Z","event":"CarrierJumpRequest","CarrierID":700,"SystemName":"Achenar"}
            {"timestamp":"2026-11-01T10:00:06Z","event":"StoredModules","StarSystem":"Sol","StationName":"Synthetic Port","Items":[{"Name":"$int_fuelscoop_size6_class5_name;","Name_Localised":"Fuel Scoop","StarSystem":"Achenar","MarketID":100,"StorageSlot":1}]}
            {"timestamp":"2026-11-01T10:00:07Z","event":"Loadout","ShipID":1,"Ship":"mandalay","ShipName":"Synthetic Explorer","Modules":[{"Slot":"FrameShiftDrive","Item":"int_hyperdrive_overcharge_size5_class5","Engineering":{"BlueprintName":"FSD_LongRange","Level":5,"ExperimentalEffect":"special_fsd_heavy"}}]}

            """);
        try
        {
            var records = new OnDemandGameRecords(() => root);
            await Turn("Where was my carrier last recorded locally? I don't need a web search.", records, key, "carrier", "Sol");
            await Turn("Where is my carrier now?", records, key, "carrier", "Colonia", research: true);
            await Turn("Do I have a spare fuel scoop in storage, and where?", records, key, "modules", "Achenar");
            await Turn("What engineering is on my current ship's FSD?", records, key, "modules", "Mass Manager");
            await Turn("Hello, just testing the microphone.", records, key, null, null);
            Console.WriteLine("PASS: conditional lookups, local modules, public carrier fallback, and no lookup for greeting.");
        }
        finally { Directory.Delete(root, true); } // Only this uniquely created synthetic fixture.
    }
    private static async Task Turn(string question, IGameRecordSource records, string key, string? expectedTopic, string? expectedAnswer, bool research = false)
    {
        var calls = new ConcurrentQueue<string>();
        var answers = new ConcurrentQueue<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new RealtimeVoiceSession(() => new RealtimeWebSocketTransport(), () => key, () => "marin",
            async (call, token) =>
            {
                calls.Enqueue(call.Name == "lookup_game_records" ? "records:" + call.Records : call.Name);
                return call.Name switch
                {
                    "get_game_context" => "{\"currentSystem\":\"Sol\",\"note\":\"Specialized carrier/module data is available through lookup_game_records.\"}",
                    "lookup_game_records" => (await records.LookupAsync(call.Records!.Value, token, call.RecordShipId)).Json,
                    "research_elite" => "{\"answer\":\"Inara last reported Synthetic Horizon TST-123 in Colonia; it is a community report, not live confirmation.\",\"clipboardCopied\":false}",
                    _ => "{\"error\":\"This synthetic test has no other tools.\"}"
                };
            });
        session.TranscriptReceived += (_, role, text) => { if (role == "assistant") answers.Enqueue(text); };
        session.StatusChanged += s => { if (s.Text == "Conversation ready") done.TrySetResult(); };
        session.Faulted += f => done.TrySetException(new InvalidOperationException(f.Message));
        var turn = await session.BeginTurnAsync(new Dictionary<string, int>());
        await session.SubmitTextAsync(turn, question);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(55));
        var answer = string.Join(" ", answers);
        Console.WriteLine("QUESTION: " + question + "\nTOOLS: " + string.Join(", ", calls) + "\nANSWER: " + answer);
        if (expectedTopic is not null && !calls.Any(c => c.Equals("records:" + expectedTopic, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Missing expected on-demand topic.");
        if (expectedTopic is null && !calls.IsEmpty) throw new InvalidOperationException("Greeting triggered a lookup.");
        if (research && !calls.Contains("research_elite")) throw new InvalidOperationException("Missing public carrier fallback.");
        if (expectedAnswer is not null && !answer.Contains(expectedAnswer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reply did not use supplied evidence.");
        if (answer.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 80)
            throw new InvalidOperationException("Reply was too verbose.");
    }
}
