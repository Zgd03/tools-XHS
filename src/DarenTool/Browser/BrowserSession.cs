using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DarenTool.Core;

namespace DarenTool.Browser;

/// <summary>Own browser profile; no browser extension, external debugging port or imported cookies.</summary>
public sealed class BrowserSession : IDisposable
{
    private readonly bool pgy;
    private readonly Window window;
    private readonly WebView2 web;
    private readonly TextBlock hint;
    private readonly TextBox address;
    private readonly Dictionary<string, TaskCompletionSource<JsonNode>> commands = [];
    private readonly Dictionary<string, JsonNode> notes = [];
    private readonly Dictionary<string, TaskCompletionSource<Dictionary<string, long>>> noteWaiters = [];
    private int directFailures;
    private bool directUnavailable;
    private long lastNoteStart;
    public string LastCollectionRoute { get; private set; } = "";
    public string LastCollectionDiagnostic { get; private set; } = "";
    public void BeginCollection() { directFailures = 0; directUnavailable = false; lastNoteStart = 0; }
    private async Task WaitNoteSlotAsync(CancellationToken ct)
    {
        if (lastNoteStart != 0)
        {
            var remaining = TimeSpan.FromMilliseconds(1200) - Stopwatch.GetElapsedTime(lastNoteStart);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
        }
        ct.ThrowIfCancellationRequested();
        lastNoteStart = Stopwatch.GetTimestamp();
    }
    private Task? initialization;
    private bool disposing;
    private long reportSequence;
    private JsonObject? reportRequest;
    private JsonNode? reportResponse;
    public string BrandSummary { get; private set; } = "尚未读取品牌";
    public event Action<string>? BrandChanged;
    public bool HasReport => reportRequest is not null && reportResponse is not null;

    public BrowserSession(bool isPgy, Window owner)
    {
        pgy = isPgy;
        window = new Window { Title = isPgy ? "蒲公英 · 登录并选择品牌" : "小红书 · 登录与笔记", Owner = owner, Width = 1180, Height = 840, MinWidth = 800, MinHeight = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        web = new WebView2();
        var dock = new DockPanel();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
        var home = new Button { Content = isPgy ? "笔记报告" : "小红书首页" };
        home.Click += async (_, _) => { try { await NavigateAsync(Home, CancellationToken.None); } catch (Exception ex) { ShowError(ex.Message); } };
        var refresh = new Button { Content = "刷新" }; refresh.Click += (_, _) => web.CoreWebView2?.Reload();
        var done = new Button { Content = "完成，返回主窗口" }; done.Click += (_, _) => window.Hide();
        toolbar.Children.Add(home); toolbar.Children.Add(refresh); toolbar.Children.Add(done);
        DockPanel.SetDock(toolbar, Dock.Top); dock.Children.Add(toolbar);
        hint = new TextBlock { Margin = new Thickness(14, 0, 14, 10), TextWrapping = TextWrapping.Wrap,
            Text = isPgy ? "请在此登录。进入笔记报告，在列表的报备品牌中选择目标品牌，点击“查询”。工具会自动识别本次品牌。完成后返回主窗口。" : "请在此正常登录小红书。需要验证码时请自行完成，然后返回主窗口。处理笔记时请勿切换此窗口的页面。" };
        DockPanel.SetDock(hint, Dock.Top); dock.Children.Add(hint);
        address = new TextBox { IsReadOnly = true, FontSize = 12, Margin = new Thickness(10, 0, 10, 8), ToolTip = "当前官方页面地址" };
        DockPanel.SetDock(address, Dock.Top); dock.Children.Add(address);
        dock.Children.Add(web); window.Content = dock;
        window.Closing += (_, e) => { if (!disposing) { e.Cancel = true; window.Hide(); } };
    }

    public string Home => pgy ? "https://pgy.xiaohongshu.com/solar/post-trade/content-manage" : "https://www.xiaohongshu.com/explore";
    public Task EnsureAsync() => initialization ??= InitializeAsync();
    private async Task InitializeAsync()
    {
        // A shown visual tree is needed for WPF WebView initialization. Login always stays user-visible.
        window.Show();
        try
        {
            var folder = Path.Combine(AppPaths.Root, "Browser", pgy ? "Pgy" : "Xhs");
            var env = await CoreWebView2Environment.CreateAsync(null, folder);
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            web.CoreWebView2.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            web.CoreWebView2.DownloadStarting += (_, e) => e.Cancel = true;
            web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; if (TrustedNavigation(e.Uri)) web.CoreWebView2.Navigate(e.Uri); };
            web.CoreWebView2.NavigationStarting += (_, e) => { if (!TrustedNavigation(e.Uri)) { e.Cancel = true; hint.Text = "已阻止离开小红书官方站点的跳转。"; } };
            web.CoreWebView2.SourceChanged += (_, _) => address.Text = web.Source?.AbsoluteUri ?? "";
            web.CoreWebView2.WebMessageReceived += MessageReceived;
            web.CoreWebView2.WebResourceResponseReceived += async (_, args) =>
            {
                if (pgy || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                    uri.Host != "edith.xiaohongshu.com" || uri.AbsolutePath != "/api/sns/web/v1/feed") return;
                try
                {
                    using var stream = await args.Response.GetContentAsync();
                    if (stream is null || disposing) return;
                    using var reader = new StreamReader(stream);
                    var response = JsonNode.Parse(await reader.ReadToEndAsync());
                    if (response?["success"]?.GetValue<bool>() != true)
                    {
                        // Native response events also observe HTTP errors when the page hook cannot read a response.
                        if (response is not null && activePageNote is { } active && noteWaiters.TryGetValue(active, out var failed) &&
                            response["code"]?.ToString() == "300031" && args.Request.Content is { CanSeek: true } content)
                        {
                            var position = content.Position;
                            try
                            {
                                content.Position = 0;
                                using var bodyReader = new StreamReader(content, System.Text.Encoding.UTF8, true, 1024, true);
                                var requestBody = JsonNode.Parse(await bodyReader.ReadToEndAsync());
                                if (requestBody?["source_note_id"]?.ToString().ToLowerInvariant() == active) failed.TrySetException(Rejected(response));
                            }
                            finally { content.Position = position; }
                        }
                        return;
                    }
                    AcceptNotes(response);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException) { }
            };
            var asm = Assembly.GetExecutingAssembly();
            using var resource = asm.GetManifestResourceStream("DarenTool.Browser.bridge.js")!;
            using var reader = new StreamReader(resource);
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(await reader.ReadToEndAsync());
            await NavigateAsync(Home, CancellationToken.None, skipEnsure: true);
        }
        catch { initialization = null; throw; }
    }

    public async Task ShowAsync() { await EnsureAsync(); window.Show(); window.Activate(); }
    public void Hide() => window.Hide();
    public async Task ClearCacheAsync()
    {
        if (initialization is null) return;
        await initialization;
        await web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
    }
    private void ShowError(string text) => MessageBox.Show(window, text, "浏览器操作未完成");
    private static bool TrustedNavigation(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort &&
        (uri.Host == "xiaohongshu.com" || uri.Host.EndsWith(".xiaohongshu.com", StringComparison.OrdinalIgnoreCase) || uri.Host is "xhslink.com" or "www.xhslink.com" or "xhslink.cn" or "www.xhslink.cn");

    private void MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var u) || u.Scheme != "https" ||
            (pgy ? u.Host != "pgy.xiaohongshu.com" : u.Host is not ("www.xiaohongshu.com" or "xiaohongshu.com"))) return;
        try
        {
            var node = JsonNode.Parse(e.WebMessageAsJson)!;
            var kind = node["kind"]?.ToString();
            if (kind == "command")
            {
                var id = node["id"]?.ToString() ?? "";
                if (!commands.TryGetValue(id, out var waiter)) return;
                if (node["error"] is { } error) waiter.TrySetException(new InvalidOperationException(error.ToString()));
                else if (node["result"] is { } result) waiter.TrySetResult(result.DeepClone());
            }
            else if (kind == "report" && pgy)
            {
                var request = node["request"] as JsonObject;
                var response = node["response"];
                if (request is null || response is null) return;
                reportRequest = (JsonObject)request.DeepClone(); reportResponse = response.DeepClone(); reportSequence++;
                if (response["success"]?.GetValue<bool>() != true) { BrandSummary = "报表未成功，请登录或检查权限"; BrandChanged?.Invoke(BrandSummary); return; }
                var brands = request["brandUserIds"]?.AsArray().Select(x => x?.ToString() ?? "").ToArray() ?? [];
                var names = response["data"]?["list"]?.AsArray().Select(x => x?["reportBrandUserName"]?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? [];
                BrandSummary = brands.Length == 0 ? "尚未选择报备品牌" : $"{string.Join("、", names.Length > 0 ? names! : brands)} · 当前报表 {response["data"]?["total"]} 条";
                hint.Text = "已识别：" + BrandSummary + "。请确认品牌后返回主窗口。";
                BrandChanged?.Invoke(BrandSummary);
            }
            else if (kind == "note" && !pgy && node["response"]?["success"]?.GetValue<bool>() == true)
            {
                AcceptNotes(node["response"]!);
            }
            else if (kind == "note" && !pgy && node["request"]?["source_note_id"] is { } requestedId &&
                node["response"]?["success"]?.GetValue<bool>() == false &&
                noteWaiters.TryGetValue(requestedId.ToString().ToLowerInvariant(), out var pending))
            {
                pending.TrySetException(Rejected(node["response"]!));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { /* An unrelated or changed response is never accepted as metrics. */ }
    }

    private void AcceptNotes(JsonNode response)
    {
        foreach (var item in response["data"]?["items"]?.AsArray() ?? [])
        {
            var card = item?["note_card"];
            var id = (item?["id"] ?? card?["note_id"])?.ToString().ToLowerInvariant();
            if (id is not null && card is not null)
            {
                var cardId = card["note_id"]?.ToString().ToLowerInvariant();
                if (!string.IsNullOrEmpty(cardId) && cardId != id) continue;
                notes[id] = card.DeepClone();
                if (noteWaiters.TryGetValue(id, out var pending))
                {
                    var metrics = Counts(card["interact_info"]);
                    if (metrics.Count > 0) pending.TrySetResult(metrics);
                }
            }
        }
    }

    public async Task NavigateAsync(string url, CancellationToken ct, bool skipEnsure = false)
    {
        if (!skipEnsure) await EnsureAsync();
        if (!TrustedNavigation(url)) throw new InvalidOperationException("链接不属于允许的小红书官方站点。");
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(object? s, CoreWebView2NavigationStartingEventArgs e) { navigationId ??= e.NavigationId; }
        void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.NavigationId == navigationId) ready.TrySetResult(e.IsSuccess);
        }
        web.CoreWebView2.NavigationStarting += Starting;
        web.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            web.CoreWebView2.Navigate(url);
            var success = await ready.Task.WaitAsync(TimeSpan.FromSeconds(40), ct);
            if (!success) throw new InvalidOperationException("官方页面加载失败，请检查网络后刷新。");
        }
        finally { web.CoreWebView2.NavigationStarting -= Starting; web.CoreWebView2.NavigationCompleted -= Completed; }
    }

    public async Task<JsonNode> SignedRequestAsync(string path, JsonObject body, CancellationToken ct, int timeoutSeconds = 25)
    {
        await EnsureAsync();
        var id = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands[id] = waiter;
        var endpoint = pgy ? "https://pgy.xiaohongshu.com" : "https://edith.xiaohongshu.com";
        try
        {
            var js = $"(()=>{{if(!window.__daren) return false; window.__daren.run({JsonSerializer.Serialize(id)},{JsonSerializer.Serialize(path)},{body.ToJsonString()},{JsonSerializer.Serialize(endpoint)},{timeoutSeconds * 1000});return true;}})()";
            var started = await web.ExecuteScriptAsync(js);
            if (started != "true") throw new InvalidOperationException("官方页面尚未就绪，请登录并刷新后重试。");
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds + 2), ct);
        }
        finally
        {
            commands.Remove(id);
            if (!disposing) try { await web.ExecuteScriptAsync($"window.__daren?.cancel({JsonSerializer.Serialize(id)})"); } catch { }
        }
    }

    public async Task<Dictionary<string, long>> ReadNoteAsync(string url, string id, CancellationToken ct)
    {
        await EnsureAsync();
        LastCollectionRoute = "接口";
        LastCollectionDiagnostic = "";
        if (!directUnavailable)
        {
            try
            {
                var query = new Uri(url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split('=', 2)).GroupBy(x => Uri.UnescapeDataString(x[0])).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First().ElementAtOrDefault(1) ?? ""));
                var body = new JsonObject
                {
                    ["source_note_id"] = id, ["image_formats"] = new JsonArray("jpg", "webp", "avif"),
                    ["extra"] = new JsonObject { ["need_body_topic"] = "1" },
                    ["xsec_source"] = query.GetValueOrDefault("xsec_source", "pc_search"),
                    ["xsec_token"] = query.GetValueOrDefault("xsec_token", "")
                };
                await WaitNoteSlotAsync(ct);
                var response = await SignedRequestAsync("/api/sns/web/v1/feed", body, ct, 8);
                if (response["success"]?.GetValue<bool>() != true) throw Rejected(response);
                var matches = response["data"]?["items"]?.AsArray().Where(item =>
                    (item?["id"] ?? item?["note_card"]?["note_id"])?.ToString().ToLowerInvariant() == id).ToArray() ?? [];
                if (matches.Length != 1) throw new InvalidOperationException("笔记接口未返回唯一匹配的笔记 ID。");
                var card = matches[0]?["note_card"];
                if (card?["note_id"] is { } cardId && cardId.ToString().ToLowerInvariant() != id) throw new InvalidOperationException("笔记接口返回的 ID 冲突。");
                var metrics = Counts(card?["interact_info"]);
                if (metrics.Count == 0) throw new InvalidOperationException("笔记接口未返回可用互动数量。");
                directFailures = 0;
                return metrics;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (NoteSessionException) { throw; }
            catch (NoteUnavailableException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or JsonException)
            {
                if (ex.Message.Contains("HTTP 401") || ex.Message.Contains("HTTP 429"))
                    throw new NoteSessionException("笔记接口要求登录、验证或限流，请在小红书窗口确认后继续。" + ex.Message);
                if (++directFailures >= 2) directUnavailable = true;
                LastCollectionDiagnostic = "接口未完成：" + ex.Message;
                LastCollectionRoute = directUnavailable ? "网页兜底（本次已暂停接口尝试）" : "网页兜底";
            }
        }
        else LastCollectionRoute = "网页兜底（本次已暂停接口尝试）";
        return await ReadNotePageAsync(url, id, ct);
    }

    private static Dictionary<string, long> Counts(JsonNode? info)
    {
        var result = new Dictionary<string, long>();
        if (info is null) return result;
        foreach (var (name, snake, camel) in new[] { ("点赞", "liked_count", "likedCount"), ("收藏", "collected_count", "collectedCount"), ("评论", "comment_count", "commentCount") })
            if (NoteLinks.Count(info[snake] ?? info[camel]) is { } n) result[name] = n;
        return result;
    }

    private static Exception Rejected(JsonNode response)
    {
        var message = (response["msg"] ?? response["message"])?.ToString() ?? "请求被拒绝";
        var text = $"笔记接口拒绝请求（{response["code"]}）：{message}";
        if (response["code"]?.ToString() == "300031") return new NoteUnavailableException(text);
        return new[] { "登录", "验证", "频繁", "限流", "login", "captcha", "rate limit" }.Any(s => message.Contains(s, StringComparison.OrdinalIgnoreCase))
            ? new NoteSessionException(text) : new InvalidOperationException(text);
    }

    private async Task<Dictionary<string, long>> ReadNotePageAsync(string url, string id, CancellationToken ct)
    {
        if (!TrustedNavigation(url)) throw new InvalidOperationException("笔记页面地址无效。");
        await WaitNoteSlotAsync(ct);
        notes.Remove(id);
        var pending = new TaskCompletionSource<Dictionary<string, long>>(TaskCreationOptions.RunContinuationsAsynchronously);
        noteWaiters[id] = pending;
        activePageNote = id;
        ulong? navigationId = null;
        var domReady = false;
        void Starting(object? s, CoreWebView2NavigationStartingEventArgs e)
        {
            navigationId ??= e.NavigationId;
            if (e.NavigationId == navigationId && PageRejection(e.Uri) is { } error) pending.TrySetException(error);
        }
        void SourceChanged(object? s, CoreWebView2SourceChangedEventArgs e)
        {
            if (navigationId is not null && PageRejection(web.Source?.AbsoluteUri ?? "") is { } error) pending.TrySetException(error);
        }
        void DomReady(object? s, CoreWebView2DOMContentLoadedEventArgs e) { if (e.NavigationId == navigationId) domReady = true; }
        void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.NavigationId == navigationId && !e.IsSuccess) pending.TrySetException(new InvalidOperationException("笔记网页加载失败，请检查网络。"));
        }
        web.CoreWebView2.NavigationStarting += Starting;
        web.CoreWebView2.SourceChanged += SourceChanged;
        web.CoreWebView2.DOMContentLoaded += DomReady;
        web.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            web.CoreWebView2.Navigate(url);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            while (!pending.Task.IsCompleted)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (domReady)
                {
                    var raw = await web.ExecuteScriptAsync($"window.__daren?.noteState({JsonSerializer.Serialize(id)}) ?? null");
                    var state = JsonNode.Parse(raw);
                    if (state?["id"]?.ToString() == id && Counts(state["info"]) is { Count: > 0 } metrics) pending.TrySetResult(metrics);
                }
                await Task.WhenAny(pending.Task, Task.Delay(200, linked.Token));
            }
            return await pending.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var raw = await web.ExecuteScriptAsync("window.__daren?.diagnostic() ?? null");
            var state = JsonNode.Parse(raw);
            LastCollectionDiagnostic = $"页面诊断：路径 {state?["path"]}，DOM {state?["readyState"]}，初始状态 {state?["hasInitialState"]}，详情条数 {state?["detailCount"]}";
            throw new TimeoutException("18 秒内未识别到匹配笔记的数据；" + LastCollectionDiagnostic);
        }
        finally
        {
            noteWaiters.Remove(id);
            activePageNote = null;
            web.CoreWebView2.NavigationStarting -= Starting;
            web.CoreWebView2.SourceChanged -= SourceChanged;
            web.CoreWebView2.DOMContentLoaded -= DomReady;
            web.CoreWebView2.NavigationCompleted -= Completed;
            // Stop remaining images/media only after obtaining the requested data, or on cancellation.
            web.CoreWebView2.Stop();
        }
    }

    private string? activePageNote;
    private static Exception? PageRejection(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Host is not ("www.xiaohongshu.com" or "xiaohongshu.com")) return null;
        if (!uri.AbsolutePath.StartsWith("/404", StringComparison.OrdinalIgnoreCase)) return null;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2))
            .GroupBy(x => Uri.UnescapeDataString(x[0])).ToDictionary(g => g.Key, g => Uri.UnescapeDataString((g.First().ElementAtOrDefault(1) ?? "").Replace("+", " ")));
        var code = query.GetValueOrDefault("error_code", "未提供错误码");
        var message = query.GetValueOrDefault("error_msg", "笔记页面不可访问");
        if (message.Length > 160) message = message[..160];
        var text = $"小红书错误页（{code}）：{message}。未取得的互动数据保留原值。";
        // An unavailable note is not evidence that the account as a whole has expired.
        if (code == "300031") return new NoteUnavailableException(text);
        return new[] { "登录", "验证", "频繁", "限流" }.Any(message.Contains) ? new NoteSessionException(text) : new NoteUnavailableException(text);
    }

    private async Task<bool> ClickPageAsync(string action) => await web.ExecuteScriptAsync($"window.__daren?.clickPage({JsonSerializer.Serialize(action)}) ?? false") == "true";
    private async Task<(JsonObject request, JsonNode response)> WaitReportAsync(long after, CancellationToken ct)
    {
        var end = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            if (reportSequence > after && reportRequest is not null && reportResponse is not null)
                return ((JsonObject)reportRequest.DeepClone(), reportResponse.DeepClone());
            await Task.Delay(200, ct);
        }
        throw new InvalidOperationException("等待蒲公英查询超时。请检查是否需要重新登录或完成验证。");
    }

    private async Task<(JsonObject request, JsonNode response)> RefreshReportAsync(CancellationToken ct)
    {
        await EnsureAsync();
        // Always refresh through the real page before reading; never silently consume an old captured report.
        var seq = reportSequence;
        if (!await ClickPageAsync("query")) throw new InvalidOperationException("请在蒲公英窗口打开笔记报告、选择报备品牌，并点击“查询”后重试。");
        var captured = await WaitReportAsync(seq, ct);
        var brands = captured.request["brandUserIds"]?.AsArray().Select(x => x?.ToString() ?? "").ToHashSet() ?? [];
        if (brands.Count == 0 || brands.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "^[0-9a-f]{24}$"))) throw new InvalidOperationException("请先选择有效的报备品牌。");
        if (captured.response["success"]?.GetValue<bool>() != true || captured.response["data"]?["list"] is not JsonArray)
            throw new InvalidOperationException("蒲公英报表未成功，请检查登录和权限。");
        return captured;
    }

    public async Task<string> DetectBrandAsync(CancellationToken ct)
    {
        await RefreshReportAsync(ct);
        return BrandSummary;
    }

    public async Task<Dictionary<string, ReportNote>> ReadReportsAsync(Action<string> log, CancellationToken ct)
    {
        var captured = await RefreshReportAsync(ct);
        var brands = captured.request["brandUserIds"]!.AsArray().Select(x => x!.ToString()).ToHashSet();
        long seq;
        var filters = (JsonObject)captured.request.DeepClone();
        foreach (var key in new[] { "operatorUserIds", "kolIds", "cooperateType", "sorts", "spuIds", "collectionTypeList" }) filters[key] = new JsonArray();
        foreach (var key in new[] { "bizTitle", "keyword", "noteId" }) filters[key] = "";
        filters["dateType"] = "2"; filters["sceneType"] = 0;
        filters["pageNum"] = 1; filters["pageSize"] = 30;
        bool signed = true;
        JsonNode response;
        try { response = await SignedRequestAsync("/api/solar/content/note/list", filters, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            signed = false;
            log("网页签名调用不可用，改用官方页面翻页：" + ex.Message);
            // UI fallback must not quietly narrow the brand dataset using a name/date/project search.
            foreach (var key in new[] { "operatorUserIds", "kolIds", "cooperateType", "spuIds", "collectionTypeList" })
                if (captured.request[key] is JsonArray arr && arr.Count > 0) throw new InvalidOperationException("请清除蒲公英报告中的其他筛选，只保留报备品牌后再试。");
            if (new[] { "bizTitle", "keyword", "noteId" }.Any(k => !string.IsNullOrEmpty(captured.request[k]?.ToString())) || captured.request["dateType"]?.ToString() != "2")
                throw new InvalidOperationException("页面翻页模式需要清除笔记、项目、关键词、日期筛选，只保留报备品牌。");
            if (captured.request["pageNum"]?.GetValue<int>() != 1)
            {
                seq = reportSequence;
                if (!await ClickPageAsync("first")) throw new InvalidOperationException("请将蒲公英笔记报告切换到第 1 页后重试。");
                captured = await WaitReportAsync(seq, ct);
            }
            response = captured.response;
        }
        var notesById = new Dictionary<string, ReportNote>();
        int? total = null; int? pageSize = null; int received = 0;
        for (var page = 1; ; page++)
        {
            ct.ThrowIfCancellationRequested();
            if (response["success"]?.GetValue<bool>() != true) throw new InvalidOperationException("蒲公英报表被拒绝：" + (response["msg"] ?? response["message"])?.ToString());
            var data = response["data"] as JsonObject ?? throw new InvalidOperationException("蒲公英报表结构已变化。");
            var list = data["list"] as JsonArray ?? throw new InvalidOperationException("蒲公英缺少报表列表。");
            var currentTotal = data["total"]?.GetValue<int>() ?? -1;
            var currentSize = data["pageSize"]?.GetValue<int>() ?? 0;
            if (data["pageNum"]?.GetValue<int>() != page || currentSize < 1 || currentTotal < 0) throw new InvalidOperationException("蒲公英返回页码、页长或总数不正确。");
            total ??= currentTotal; pageSize ??= currentSize;
            if (total != currentTotal || pageSize != currentSize || list.Count > pageSize || page > 10000) throw new InvalidOperationException("报表采集期间总数或分页发生变化，请重新读取。");
            var pages = Math.Max(1, (int)Math.Ceiling(total.Value / (double)pageSize.Value));
            if (data["totalPage"] is { } tp && total > 0 && tp.GetValue<int>() != pages) throw new InvalidOperationException("蒲公英报表总页数不一致。");
            foreach (var row in list)
            {
                var id = row?["noteId"]?.ToString().ToLowerInvariant() ?? "";
                var brandId = row?["reportBrandUserId"]?.ToString() ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{24}$") || !brands.Contains(brandId)) throw new InvalidOperationException("报表返回了无效笔记或其他品牌数据，已停止回填。");
                var note = new ReportNote(id, brandId, row?["reportBrandUserName"]?.ToString() ?? "", NoteLinks.Count(row?["impNum"], false), NoteLinks.Count(row?["readNum"], false), row?["dateKey"]?.ToString() ?? "");
                if (notesById.TryGetValue(id, out var old) && old != note) throw new InvalidOperationException("同一笔记存在冲突报表，已停止回填。");
                notesById[id] = note;
            }
            received += list.Count;
            log($"蒲公英：第 {page}/{pages} 页，已取得 {received}/{total} 条。");
            if (page >= pages) break;
            if (list.Count == 0) throw new InvalidOperationException("蒲公英提前返回空页。");
            await Task.Delay(400, ct);
            if (signed)
            {
                filters["pageNum"] = page + 1;
                response = await SignedRequestAsync("/api/solar/content/note/list", filters, ct);
            }
            else
            {
                seq = reportSequence;
                if (!await ClickPageAsync("next")) throw new InvalidOperationException("未找到唯一的下一页按钮。请反馈当前蒲公英页面兼容问题。");
                captured = await WaitReportAsync(seq, ct);
                var currentBrands = captured.request["brandUserIds"]?.AsArray().Select(x => x?.ToString() ?? "").ToHashSet() ?? [];
                if (!brands.SetEquals(currentBrands)) throw new InvalidOperationException("采集期间品牌发生变化，已停止回填。");
                response = captured.response;
            }
        }
        if (received != total) throw new InvalidOperationException("取得条数与报表总数不一致，已停止回填。");
        return notesById;
    }

    public void Dispose()
    {
        disposing = true;
        foreach (var command in commands.Values) command.TrySetCanceled();
        web.Dispose(); window.Close();
    }
}
