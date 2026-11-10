using System.Net;
using Alexandra.App;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Microsoft.Playwright;

if (args.Contains("--enable-local-logging"))
{
    var settings = new SettingsStore();
    settings.Value.DiagnosticLoggingEnabled = true;
    settings.Value.ResearchBrowserEnabled = true;
    settings.Save();
    Console.WriteLine("Enabled local diagnostics and isolated research browsing. Other settings and the API key were preserved.");
    return;
}
if (args.Contains("--live"))
{
    var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No API key configured.");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    var log = new ConsoleLog();
    var companion = new OpenAiCompanion(http, () => key, () => "gpt-5.4", () => "coral",
        () => new ResearchBrowser(log), () => true, log);
    var answer = await companion.AnswerAsync(
        "Use the research_browser tool to open https://www.elitedangerous.com/news and tell me the first news headline visible on that page. Do not recommend travel. If browser access fails, say so rather than substituting a search result.",
        new(null,"{}","Synthetic test, no user data"),[], _=>throw new InvalidOperationException("No capture in this test"), CancellationToken.None);
    Console.WriteLine("Answer: " + answer.Speech);
    Console.WriteLine("Sources: " + string.Join(", ", answer.Sources.Select(s=>s.Url)));
    return;
}
// Synthetic GET search page. All navigation is fulfilled locally inside the isolated browser;
// it never contacts Inara or attempts the real site's access check.
var requests = new List<string>();
await using var browser = new ResearchBrowser {
    ConfigureTestContext = context => context.RouteAsync("https://inara.cz/**", async route => {
        requests.Add(route.Request.Url);
        var uri = new Uri(route.Request.Url);
        var html = uri.Query.Contains("blocked")
            ? "<html><title>INARA</title><body>Access check required. Confirm your visit. <button>Confirm and continue</button></body></html>"
            : """
                <html><title>Synthetic commodity search</title><body>
                <h1>Commodity search fixture</h1>
                <form method="get" action="/elite/commodities/">
                  <label>Near star system <input name="system" type="text"></label>
                  <label>Commodity <select name="commodity"><option>Silver</option><option>Gold</option></select></label>
                  <label>Large pad <input type="checkbox" name="large"></label>
                  <button type="submit">Search</button>
                </form>
                <a href="/elite/station/123/">Test station details</a>
                <a href="/elite/cmdr/123/">Private account</a>
                <form method="post" action="/elite/commodities/"><input type="password" name="password"><button>Send</button></form>
                <script>if(location.search)document.body.appendChild(document.createTextNode('RESULT ' + decodeURIComponent(location.search)));</script>
                </body></html>
                """;
        await route.FulfillAsync(new() { Status=200,ContentType="text/html",Body=html });
    })
};
var snapshot = await browser.ActAsync(new("open","https://inara.cz/elite/commodities/"),CancellationToken.None);
if(snapshot.BlockedReason is not null || !snapshot.Text.Contains("fixture")) throw new Exception("Open/read failed");
if(snapshot.Controls.Any(c=>c.Label.Contains("Private account") || c.Label=="Send")) throw new Exception("Unsafe controls exposed");
var original = snapshot.Snapshot;
var field = snapshot.Controls.Single(c=>c.Kind=="input");
snapshot = await browser.ActAsync(new("fill",Snapshot:snapshot.Snapshot,Element:field.Id,Value:"Sol"),CancellationToken.None);
try { await browser.ActAsync(new("fill",Snapshot:original,Element:field.Id,Value:"stale"),CancellationToken.None); throw new Exception("Stale control accepted"); }
catch(InvalidOperationException ex) when(ex.Message.Contains("stale")) { }
var select = snapshot.Controls.Single(c=>c.Kind=="select");
snapshot = await browser.ActAsync(new("select",Snapshot:snapshot.Snapshot,Element:select.Id,Value:"Gold"),CancellationToken.None);
var box = snapshot.Controls.Single(c=>c.Kind=="checkbox");
snapshot = await browser.ActAsync(new("click",Snapshot:snapshot.Snapshot,Element:box.Id),CancellationToken.None);
var submit = snapshot.Controls.Single(c=>c.Kind=="submit");
snapshot = await browser.ActAsync(new("click",Snapshot:snapshot.Snapshot,Element:submit.Id),CancellationToken.None);
if(!snapshot.Text.Contains("system=Sol") || !snapshot.Text.Contains("commodity=Gold")) throw new Exception("Search not submitted");
Console.WriteLine("PASS: browser open/read/fill/select/checkbox/search; stale controls and account/write controls blocked.");
snapshot = await browser.ActAsync(new("open","https://inara.cz/elite/commodities/?blocked=1"),CancellationToken.None);
if(snapshot.BlockedReason is null || snapshot.Controls.Count != 0) throw new Exception("Access-check page was not blocked");
var before = requests.Count;
await browser.ActAsync(new("open","https://inara.cz/elite/commodities/"),CancellationToken.None);
if(requests.Count != before) throw new Exception("Browser continued after an access check");
Console.WriteLine("PASS: access check stops further browsing. No live Inara requests were made.");

sealed class ConsoleLog : IDiagnosticLog
{
    public void Write(string kind,string message,int? turn=null) =>
        Console.WriteLine(kind + ": " + LocalDiagnosticLog.Sanitize(message));
}

