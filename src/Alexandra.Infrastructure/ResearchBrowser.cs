using System.Text.Json;
using Alexandra.Core;
using Microsoft.Playwright;

namespace Alexandra.Infrastructure;

// One isolated browser per research turn; no user's browser profile, desktop input, downloads or model JavaScript.
public sealed class ResearchBrowser(IDiagnosticLog? diagnostics = null) : IResearchBrowser
{
    // Internal test seam: deterministic synthetic pages, never exposed as a model tool or app setting.
    internal Func<IBrowserContext, Task>? ConfigureTestContext { get; init; }
    private readonly IDiagnosticLog _log = diagnostics ?? NullDiagnosticLog.Instance;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private readonly Dictionary<string, Element> _elements = [];
    private string _snapshot = "";
    private string? _blocked;
    private int? _status;
    private int _actions;
    private DateTimeOffset _lastNavigation;
    private readonly SemaphoreSlim _gate = new(1);
    private bool _disposed;

    public async Task<BrowserSnapshot> ActAsync(BrowserAction action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ResearchBrowser));
            if (++_actions > 18) throw new InvalidOperationException("Browser research reached its 18-action limit. Summarize the verified findings.");
            cancellationToken.ThrowIfCancellationRequested();
            if (action.Action == "open" && !BrowserPolicy.CanNavigate(action.Url))
                throw new InvalidOperationException("This browser only opens approved public Elite research pages. Account pages and other sites are not allowed; use web search for other sources.");
            await EnsureBrowserAsync(cancellationToken);
            using var registration = cancellationToken.Register(() => { _ = CloseBrowserAsync(); });
            if (_blocked is not null) return await SnapshotAsync();
            switch (action.Action)
            {
                case "open":
                    await PaceAsync(cancellationToken);
                    var response = await _page!.GotoAsync(action.Url!, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                    _status = response?.Status;
                    break;
                case "read":
                    break;
                case "click":
                case "fill":
                case "select":
                    if (action.Snapshot != _snapshot || action.Element is null || !_elements.TryGetValue(action.Element, out var element))
                        throw new InvalidOperationException("That control is stale or unknown. Read the page again and use its latest snapshot and element IDs.");
                    if (!BrowserPolicy.CanNavigate(_page!.Url))
                        throw new InvalidOperationException("The page moved outside the allowed research area.");
                    var locator = _page.Locator("[data-alex-ref='" + element.Id + "']");
                    if (await locator.CountAsync() != 1) throw new InvalidOperationException("The page changed. Read it again.");
                    if (action.Action == "fill")
                    {
                        if (element.Kind != "input" || action.Value is null || action.Value.Length > 200)
                            throw new InvalidOperationException("Only short search-field values are allowed.");
                        await locator.FillAsync(action.Value, new() { Timeout = 5000 });
                    }
                    else if (action.Action == "select")
                    {
                        if (element.Kind != "select" || action.Value is null || action.Value.Length > 200)
                            throw new InvalidOperationException("Use an observed select field and an exact option label.");
                        await locator.SelectOptionAsync(new SelectOptionValue { Label = action.Value }, new() { Force = true, Timeout = 5000 });
                    }
                    else if (element.Kind == "link")
                    {
                        if (!BrowserPolicy.CanNavigate(element.Url)) throw new InvalidOperationException("That link is outside the research scope.");
                        await PaceAsync(cancellationToken);
                        var result = await _page.GotoAsync(element.Url!, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                        _status = result?.Status;
                    }
                    else if (element.Kind is "submit" or "radio" or "checkbox" or "option")
                    {
                        await PaceAsync(cancellationToken);
                        await locator.ClickAsync(new() { Timeout = 5000 });
                        try { await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 5000 }); }
                        catch (TimeoutException) { }
                    }
                    else throw new InvalidOperationException("That control is not a supported research action.");
                    break;
                default: throw new InvalidOperationException("Unknown browser action.");
            }
            // Give ordinary search widgets time to render; bounded and cancellable.
            await Task.Delay(250, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await SnapshotAsync();
            _log.Write("browser", JsonSerializer.Serialize(new { action.Action, snapshot.Url, snapshot.Title, snapshot.BlockedReason }));
            return snapshot;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException && cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            _log.Write("browser.error", ex.GetType().Name + ": browser navigation or control unavailable");
            throw new InvalidOperationException("The research browser could not load or operate that page. Do not claim the lookup succeeded.");
        }
        finally { _gate.Release(); }
    }
    private async Task PaceAsync(CancellationToken token)
    {
        var remaining = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - _lastNavigation);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
        _lastNavigation = DateTimeOffset.UtcNow;
    }
    private async Task EnsureBrowserAsync(CancellationToken token)
    {
        if (_page is not null) return;
        _playwright = await Playwright.CreateAsync();
        token.ThrowIfCancellationRequested();
        // Edge is already installed on supported Windows systems. This launches a separate, hidden browser.
        _browser = await _playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = true, Timeout = 15000 });
        token.ThrowIfCancellationRequested();
        _context = await _browser.NewContextAsync(new() {
            AcceptDownloads = false, ServiceWorkers = ServiceWorkerPolicy.Block,
            Permissions = [], Locale = "en-US", ViewportSize = new() { Width = 1280, Height = 900 }
        });
        await _context.RouteAsync("**/*", async route => {
            var request = route.Request;
            if (BrowserPolicy.CanRequest(request.Url, request.Method, request.ResourceType, request.IsNavigationRequest))
                await route.ContinueAsync();
            else await route.AbortAsync();
        });
        await _context.RouteWebSocketAsync("**/*", socket => socket.CloseAsync());
        if (ConfigureTestContext is not null) await ConfigureTestContext(_context);
        _page = await _context.NewPageAsync();
        _page.SetDefaultTimeout(6000);
        _page.Response += (_, response) => {
            if (response.Request.IsNavigationRequest && response.Frame == _page.MainFrame) _status = response.Status;
        };
        _page.Dialog += async (_, dialog) => await dialog.DismissAsync();
        _page.Popup += async (_, popup) => await popup.CloseAsync();
        _page.Download += async (_, download) => await download.CancelAsync();
    }
    private async Task<BrowserSnapshot> SnapshotAsync()
    {
        if (_page is null) throw new InvalidOperationException("Open a research page first.");
        var title = await _page.TitleAsync();
        var text = await _page.Locator("body").InnerTextAsync(new() { Timeout = 6000 });
        if (_blocked is null && BrowserPolicy.IsBlockedPage(title, text, _status))
            _blocked = "The site requires human verification or has denied/rate-limited access. Stop here; do not bypass or solve the check. Use available public search sources and disclose this limitation.";
        if (!BrowserPolicy.CanNavigate(_page.Url) && _page.Url != "about:blank")
            _blocked = "The page redirected outside the permitted public research area.";
        _elements.Clear();
        _snapshot = Guid.NewGuid().ToString("N")[..10];
        if (_blocked is not null) return new(_snapshot, _page.Url, title, "", [], _blocked);
        var values = await _page.EvaluateAsync<JsonElement>(ReadControlsScript);
        foreach (var value in values.EnumerateArray())
        {
            var element = JsonSerializer.Deserialize<Element>(value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            if (!BrowserPolicy.SafeLabel(element.Label)) continue;
            if (element.Kind == "link" && !BrowserPolicy.CanNavigate(element.Url)) continue;
            if (element.Kind == "submit" && (element.Method != "get" || !BrowserPolicy.CanNavigate(element.FormAction))) continue;
            if (element.Kind == "input" && !BrowserPolicy.SafeLabel(element.Name ?? "")) continue;
            _elements[element.Id] = element;
        }
        return new(_snapshot, _page.Url, title, text[..Math.Min(text.Length, 22000)],
            _elements.Values.Take(150).Select(e => new BrowserControl(e.Id, e.Kind, e.Label, e.Url, e.Value, e.Options ?? [])).ToArray());
    }
    // Fixed app-owned DOM reader. Never executes code, selectors or script supplied by the model.
    private const string ReadControlsScript = """
        () => {
          let n = 0;
          return Array.from(document.querySelectorAll('a[href],input,select,button,[role="option"]')).flatMap(el => {
            const tag = el.tagName.toLowerCase(), type = (el.type || '').toLowerCase();
            const visible = el.getClientRects().length > 0;
            if ((!visible && tag !== 'select') || el.disabled) return [];
            if (tag === 'input' && !['text','search','number','radio','checkbox','submit'].includes(type)) return [];
            let kind = tag === 'a' ? 'link' : tag === 'select' ? 'select'
              : el.getAttribute('role') === 'option' ? 'option'
              : type === 'radio' || type === 'checkbox' ? type
              : type === 'submit' || (tag === 'button' && el.form && type !== 'button') ? 'submit'
              : tag === 'input' ? 'input' : '';
            if (!kind) return [];
            const label = (Array.from(el.labels || []).map(l=>l.innerText).join(' ') ||
              el.getAttribute('aria-label') || el.getAttribute('placeholder') || el.innerText || el.value || el.name || tag).trim().slice(0,250);
            const id = String(++n); el.setAttribute('data-alex-ref',id);
            return [{ id, kind, label, name:el.name || '', url:tag==='a'?el.href:null,
              value:el.value || null, method:(el.form?.method || '').toLowerCase(),
              formAction:el.form?.action || null,
              options:tag==='select' ? Array.from(el.options).slice(0,80).map(o=>({value:o.value,label:o.text})) : [] }];
          }).slice(0,300);
        }
        """;
    private sealed record Element(string Id, string Kind, string Label, string? Name, string? Url,
        string? Value, string? Method, string? FormAction, BrowserOption[]? Options);
    private async Task CloseBrowserAsync()
    {
        try { if (_browser is not null) await _browser.CloseAsync(); } catch (PlaywrightException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseBrowserAsync();
        _playwright?.Dispose();
    }
}

