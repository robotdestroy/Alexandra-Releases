using System.Net;
using System.Text;
using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;
namespace Alexandra.Tests;

public sealed class ResearchTests
{
    [Theory]
    [InlineData("https://inara.cz/elite/commodities/")]
    [InlineData("https://inara.cz/elite/nearest-stations/?ps1=Sol")]
    [InlineData("https://www.edsm.net/en/system/id/27/name/Sol")]
    [InlineData("https://www.elitedangerous.com/news")]
    public void PublicResearchPagesAreAllowed(string url) => Assert.True(BrowserPolicy.CanNavigate(url));
    [Theory]
    [InlineData("http://inara.cz/elite/commodities/")]
    [InlineData("https://inara.cz.evil.test/elite/commodities/")]
    [InlineData("https://inara.cz@localhost/elite/commodities/")]
    [InlineData("file:///C:/private.txt")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://10.0.0.1/")]
    [InlineData("https://inara.cz:8443/elite/commodities/")]
    [InlineData("https://inara.cz/elite/cmdr/123/")]
    [InlineData("https://inara.cz/elite/commodities/?action=delete")]
    [InlineData("https://inara.cz/validatechallenge.php")]
    [InlineData("https://elite-dangerous.fandom.com/wiki/Special:UserLogin")]
    public void PrivateAndMutatingPagesAreDenied(string url) => Assert.False(BrowserPolicy.CanNavigate(url));
    [Fact]
    public void NetworkBlocksWritesTrackingAndMedia()
    {
        Assert.False(BrowserPolicy.CanRequest("https://inara.cz/elite/commodities/", "POST", "document", true));
        Assert.False(BrowserPolicy.CanRequest("https://tracking.example/test.js", "GET", "script", false));
        Assert.False(BrowserPolicy.CanRequest("https://inara.cz/audio.mp3", "GET", "media", false));
        Assert.True(BrowserPolicy.CanRequest("https://inara.cz/js/site.js", "GET", "script", false));
    }
    [Theory]
    [InlineData("INARA", "Access check required. Confirm your visit to continue.")]
    [InlineData("Just a moment", "Loading")]
    [InlineData("Site", "Please verify you are human.")]
    public void AccessChecksStopBrowserActions(string title, string text) => Assert.True(BrowserPolicy.IsBlockedPage(title, text));
    [Fact]
    public void OrdinaryContentIsNotAnAccessCheck() => Assert.False(BrowserPolicy.IsBlockedPage("Commodities", "Gold 1000 units, updated 5 minutes ago"));
    [Fact]
    public async Task BrowserFunctionLoopKeepsReasoningAndAddsOnlyVerifiedSource()
    {
        var requests = new List<JsonElement>();
        using var http = new HttpClient(new Handler(async r => {
            using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
            requests.Add(doc.RootElement.Clone());
            return Json(requests.Count == 1 ? CallResponse : FinalResponse);
        }));
        var browser = new FakeBrowser();
        var log = new MemoryLog();
        var companion = new OpenAiCompanion(http, () => "test", () => "gpt-5.4", () => "coral", () => browser, () => true, log);
        var answer = await companion.AnswerAsync("Find gold", new(null,"{}","Test"), [], _ => throw new Exception(), CancellationToken.None);
        Assert.Equal(2, requests.Count);
        Assert.Contains(requests[1].GetProperty("input").EnumerateArray(), e => e.TryGetProperty("type", out var t) && t.GetString() == "reasoning");
        Assert.Contains(requests[1].GetProperty("input").EnumerateArray(), e => e.TryGetProperty("type", out var t) && t.GetString() == "function_call_output");
        Assert.True(AnswerPolicy.HasDestinationEvidence(answer));
        Assert.True(browser.Disposed);
        Assert.Contains(log.Items, s => s.Contains("research.browser_action"));
        Assert.False(requests[1].GetProperty("store").GetBoolean());
    }
    [Fact]
    public async Task BlockedBrowserPageCannotBecomeDestinationEvidence()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(++count == 1 ? CallResponse : FinalResponse))));
        var browser = new FakeBrowser { Blocked = true };
        var companion = new OpenAiCompanion(http, () => "test", () => "gpt-5.4", () => "coral", () => browser, () => true);
        var answer = await companion.AnswerAsync("Find gold", new(null,"{}","Test"), [], _ => throw new Exception(), CancellationToken.None);
        Assert.False(AnswerPolicy.HasDestinationEvidence(answer));
        Assert.True(browser.Disposed);
    }
    [Fact]
    public async Task CancelledBrowserIsDisposedAndNoAnswerReturned()
    {
        using var cancel = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(CallResponse))));
        var browser = new FakeBrowser { OnAct = () => cancel.Cancel() };
        var companion = new OpenAiCompanion(http, () => "test", () => "gpt-5.4", () => "coral", () => browser, () => true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => companion.AnswerAsync("Find gold", new(null,"{}","Test"), [], _ => throw new Exception(), cancel.Token));
        Assert.True(browser.Disposed);
    }
    [Fact]
    public void LogsAreOptInBoundedAndRedactCredentials()
    {
        var dir = Directory.CreateTempSubdirectory("alexandra-log-test-").FullName;
        try
        {
            var enabled = false;
            var log = new LocalDiagnosticLog(dir, () => enabled);
            log.Write("transcript.user", "not recorded");
            Assert.Empty(Directory.GetFiles(dir));
            enabled = true;
            log.Write("transcript.user", "hello sk-proj-abcdefghijklmnop Bearer abcdefg api_key=secret123 data:image/jpeg;base64,AAAA");
            var text = log.ReadRecent();
            Assert.Contains("hello", text);
            Assert.DoesNotContain("abcdefghijklmnop", text);
            Assert.DoesNotContain("abcdefg", text);
            Assert.DoesNotContain("secret123", text);
            Assert.DoesNotContain("base64", text);
            log.Write("long", new string('x', 20000));
            Assert.Contains("truncated", log.ReadRecent());
            File.WriteAllText(Path.Combine(dir, "unrelated.txt"), "preserve");
            log.Clear();
            Assert.Empty(Directory.GetFiles(dir, "session-*.jsonl"));
            Assert.True(File.Exists(Path.Combine(dir, "unrelated.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void OldLogsExpireOnWrite()
    {
        var dir = Directory.CreateTempSubdirectory("alexandra-log-test-").FullName;
        try
        {
            var old = Path.Combine(dir,"session-old-000.jsonl");
            File.WriteAllText(old,"expired"); File.SetLastWriteTimeUtc(old,DateTime.UtcNow.AddDays(-8));
            var log = new LocalDiagnosticLog(dir, () => true);
            log.Write("test","new");
            Assert.False(File.Exists(old));
            Assert.Contains("new",log.ReadRecent());
        }
        finally { Directory.Delete(dir,true); }
    }
    private const string CallResponse = """
      {"status":"completed","output":[
        {"type":"reasoning","id":"rs_test","summary":[],"encrypted_content":"test-encrypted"},
        {"type":"function_call","name":"research_browser","call_id":"c1","arguments":"{\"action\":\"open\",\"url\":\"https://inara.cz/elite/commodities/\",\"snapshot\":null,\"element\":null,\"value\":null}"}]}
      """;
    private static string FinalResponse => JsonSerializer.Serialize(new { status="completed", output=new[] {new {type="message", content=new[] {new {type="output_text",text=JsonSerializer.Serialize(new {
        speech="Test station in Sol has gold. Data age is five minutes.",destination=new {system="Sol",station="Test station",evidenceUrl="https://inara.cz/elite/commodities/"}
    })}}}}});
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text,Encoding.UTF8,"application/json") };
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t) { t.ThrowIfCancellationRequested(); return send(r); } }
    private sealed class FakeBrowser : IResearchBrowser
    {
        public bool Blocked { get; init; }
        public bool Disposed { get; private set; }
        public Action? OnAct { get; init; }
        public Task<BrowserSnapshot> ActAsync(BrowserAction a,CancellationToken t)
        { OnAct?.Invoke(); t.ThrowIfCancellationRequested(); return Task.FromResult(new BrowserSnapshot("s1",a.Url!,"Commodities",Blocked ? "" : "Gold at Test station, Sol, updated 5 minutes ago",[],Blocked ? "Access check required" : null)); }
        public ValueTask DisposeAsync() { Disposed=true; return ValueTask.CompletedTask; }
    }
    private sealed class MemoryLog : IDiagnosticLog
    { public List<string> Items { get; }=[]; public void Write(string k,string m,int? t=null)=>Items.Add(k+" "+m); }
}

