using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarenTool.Browser;

namespace DarenTool.Core;

public record ProcessingOutcome(string Directory, int Complete, int Partial, int Failed, bool Cancelled, bool HasWorkbook);

public sealed class ProcessingService(BrowserSession xhs, BrowserSession pgy, Action<string> log, Action<RowResult> showRow, Action<string, int, int> progress)
{
    public string? OutputDirectory { get; private set; }
    private static readonly HttpClient Redirects = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<(string Url, string Id)> ResolveAsync(string url, CancellationToken ct)
    {
        for (var n = 0; n < 8; n++)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !NoteLinks.Allowed(uri)) throw new InvalidOperationException("链接不是有效的小红书笔记地址。");
            if (uri.Scheme == "http") { uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri; url = uri.AbsoluteUri; }
            var id = NoteLinks.Id(url);
            if (id.Length > 0) return (url, id);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await Redirects.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is not { } destination) throw new InvalidOperationException("短链跳转缺少目标地址。");
                url = new Uri(uri, destination).AbsoluteUri;
            }
            else throw new InvalidOperationException($"链接未跳转到有效笔记（HTTP {(int)response.StatusCode}），可能已失效或需要验证。");
        }
        throw new InvalidOperationException("链接跳转次数过多。");
    }

    public async Task<ProcessingOutcome> RunAsync(string source, string outputRoot, ProcessMode mode, bool resume, CancellationToken ct)
    {
        var workbook = await Task.Run(() => new WorkbookFile(source, mode), ct);
        var directory = Path.Combine(outputRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss_ffffff"));
        Directory.CreateDirectory(directory);
        OutputDirectory = directory;
        var stem = Path.GetFileNameWithoutExtension(source);
        using var writer = new StreamWriter(Path.Combine(directory, "运行日志.txt"), false, new UTF8Encoding(true)) { AutoFlush = true };
        void WriteLog(string text) { var line = DateTimeOffset.Now.ToString("HH:mm:ss") + "  " + text; writer.WriteLine(line); log(line); }
        WriteLog($"达人 Excel 工具 2.0.2 优化开发版；模式 {mode}；识别 {workbook.Records.Count} 条笔记。");
        var links = new Dictionary<string, string>();
        var numeric = new Dictionary<string, long>();
        var conversion = new List<RowResult>();
        var results = new List<RowResult>();
        var resolvedLinks = new Dictionary<string, (string Url, string Id)>();
        var collected = new Dictionary<string, (Dictionary<string, long> Metrics, string Fetched)>();
        var snapshotClock = Stopwatch.StartNew();
        var wholeRun = Stopwatch.StartNew();
        xhs.BeginCollection();
        async Task<(string Url, string Id)> ResolveCached(string url)
        {
            if (!resolvedLinks.TryGetValue(url, out var resolved)) resolvedLinks[url] = resolved = await ResolveAsync(url, ct);
            return resolved;
        }
        var cancelled = false;
        var interrupted = false;
        var hasWorkbook = false;
        var checkpoint = Path.Combine(AppPaths.Root, "checkpoint.json");
        var cache = new Dictionary<string, RowResult>();
        if (resume && File.Exists(checkpoint))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<Checkpoint>(await File.ReadAllTextAsync(checkpoint, ct));
                if (stored is null || stored.Hash != workbook.SourceHash || stored.Mode != mode || DateTimeOffset.Now - stored.Started > TimeSpan.FromHours(1))
                    WriteLog("上次缓存与源文件、模式不符或已超过一小时，将重新获取。");
                else
                {
                    foreach (var row in stored.Rows.Where(r => r.Metrics.ContainsKey("点赞") && r.Metrics.ContainsKey("收藏") && r.Metrics.ContainsKey("评论") &&
                        DateTimeOffset.TryParse(r.FetchedAt, out var fetched) && DateTimeOffset.Now - fetched <= TimeSpan.FromHours(1) && fetched <= DateTimeOffset.Now))
                        cache[row.SourceUrl] = row;
                    WriteLog($"可复用 {cache.Count} 条一小时内的互动数据；蒲公英数据仍重新读取。");
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException) { WriteLog("未使用旧缓存：" + ex.Message); }
        }
        var started = DateTimeOffset.Now;
        try
        {
            if (mode is ProcessMode.Shorten or ProcessMode.Combined)
            {
                var seen = new Dictionary<string, string>();
                var index = 0;
                foreach (var record in workbook.Records)
                {
                    ct.ThrowIfCancellationRequested();
                    progress("转换并核验发布链接", ++index, workbook.Records.Count);
                    var row = NewResult(record);
                    try
                    {
                        if (workbook.LinkHasFormula(record.LinkCell)) throw new InvalidOperationException("发布链接含公式，已保留原值。");
                        if (NoteLinks.IsShort(record.Url)) { row.ResultUrl = record.Url; row.Status = "已有短链，跳过"; }
                        else
                        {
                            var id = NoteLinks.Id(record.Url);
                            if (id.Length == 0) throw new InvalidOperationException("无法从长链接提取笔记 ID。");
                            if (!seen.TryGetValue(record.Url, out var shortUrl))
                            {
                                var response = await xhs.SignedRequestAsync("/api/sns/web/short_url", new JsonObject { ["original_url"] = record.Url }, ct);
                                if (response["success"]?.GetValue<bool>() == false) throw new InvalidOperationException("短链接口拒绝请求：" + (response["msg"] ?? response["message"])?.ToString());
                                shortUrl = response["data"]?["short_url"]?.ToString().Trim() ?? "";
                                if (!shortUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) shortUrl = "https://" + shortUrl;
                                if (!NoteLinks.IsShort(shortUrl)) throw new InvalidOperationException("接口未返回有效的官方短链接。");
                                var resolved = await ResolveCached(shortUrl);
                                if (resolved.Id != id) throw new InvalidOperationException("短链接指向其他笔记，拒绝写入。");
                                // Keep the original access token for subsequent metrics collection.
                                resolvedLinks[shortUrl] = (record.Url, id);
                                resolvedLinks[record.Url] = (record.Url, id);
                                seen[record.Url] = shortUrl;
                                await Task.Delay(500, ct);
                            }
                            row.NoteId = id; row.ResultUrl = shortUrl; row.Status = "转换并核验成功";
                            links[record.LinkCell] = shortUrl;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { row.Status = "失败，保留原值"; row.Reason = ex.Message; }
                    row.FetchedAt = DateTimeOffset.Now.ToString("O");
                    conversion.Add(row); showRow(row); WriteLog($"第 {row.Row} 行 {row.Name}：{row.Status}" + (row.Reason.Length > 0 ? "；" + row.Reason : ""));
                    if (conversion.Count % 10 == 0 || snapshotClock.Elapsed >= TimeSpan.FromSeconds(2))
                    { WriteCsv(Path.Combine(directory, stem + "_转换明细.csv"), conversion); snapshotClock.Restart(); }
                }
                if (links.Count > 0)
                {
                    var suffix = mode == ProcessMode.Combined ? "_链接处理结果.xlsx" : "_短链已转换.xlsx";
                    await Task.Run(() => workbook.Save(Path.Combine(directory, stem + suffix), [], links));
                    hasWorkbook = true; WriteLog($"已保存 {links.Count} 条短链修改。");
                }
                if (mode == ProcessMode.Shorten)
                    return Outcome(directory, conversion, false, hasWorkbook, row => !row.Status.StartsWith("失败"));
            }

            Dictionary<string, ReportNote> report = [];
            var reportError = "";
            if (mode != ProcessMode.Interaction)
            {
                progress("读取蒲公英品牌报表", 0, workbook.Records.Count);
                try { report = await pgy.ReadReportsAsync(WriteLog, ct); WriteLog($"报表读取完成：{report.Count} 篇笔记。"); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { reportError = ex.Message; WriteLog("蒲公英未完成，曝光/阅读保留原值：" + reportError); }
            }
            var recordIndex = 0;
            var pcFailures = 0;
            var sessionBlocked = "";
            using var journal = new StreamWriter(Path.Combine(directory, "逐行采集记录.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
            foreach (var record in workbook.Records)
            {
                ct.ThrowIfCancellationRequested();
                progress("采集并回填笔记数据", ++recordIndex, workbook.Records.Count);
                var row = NewResult(record);
                row.ResultUrl = links.GetValueOrDefault(record.LinkCell, record.Url);
                var reasons = new List<string>();
                var rowClock = Stopwatch.StartNew();
                var resolveSeconds = 0d;
                var route = "未请求";
                var conversionFailure = conversion.FirstOrDefault(r => r.Row == row.Row && r.Status.StartsWith("失败"));
                if (conversionFailure is not null) reasons.Add("转链：" + conversionFailure.Reason);
                try
                {
                    var resolved = await ResolveCached(row.ResultUrl);
                    resolveSeconds = rowClock.Elapsed.TotalSeconds;
                    row.NoteId = resolved.Id;
                    if (collected.TryGetValue(resolved.Id, out var sameNote))
                    {
                        row.Metrics = new(sameNote.Metrics); row.FetchedAt = sameNote.Fetched;
                        route = "本次笔记复用";
                    }
                    else if (cache.TryGetValue(record.Url, out var old) && old.NoteId == resolved.Id)
                    {
                        row.Metrics = old.Metrics.Where(x => x.Key is "点赞" or "收藏" or "评论").ToDictionary(x => x.Key, x => x.Value);
                        row.FetchedAt = old.FetchedAt;
                        route = "断点缓存";
                        reasons.Add("互动数据复用一小时内缓存");
                    }
                    else if (sessionBlocked.Length > 0) reasons.Add(sessionBlocked);
                    else if (pcFailures >= 5) reasons.Add("互动数据连续 5 条失败，余下互动数据保留原值。请重新登录后继续。");
                    else
                    {
                        route = "正在请求";
                        try { row.Metrics = await xhs.ReadNoteAsync(resolved.Url, resolved.Id, ct); }
                        finally
                        {
                            route = xhs.LastCollectionRoute;
                            if (xhs.LastCollectionDiagnostic.Length > 0) WriteLog($"第 {row.Row} 行采集诊断：{xhs.LastCollectionDiagnostic}");
                        }
                        row.FetchedAt = DateTimeOffset.Now.ToString("O");
                        route = xhs.LastCollectionRoute;
                        pcFailures = row.Metrics.Count > 0 ? 0 : pcFailures + 1;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (NoteSessionException ex) { sessionBlocked = ex.Message; reasons.Add(sessionBlocked); route = "会话暂停"; WriteLog("暂停后续互动请求：" + sessionBlocked); }
                catch (NoteUnavailableException ex) { reasons.Add(ex.Message); }
                catch (Exception ex) { pcFailures++; reasons.Add(ex.Message); }
                if (row.Metrics.Count == 3) collected[row.NoteId] = (new(row.Metrics), row.FetchedAt);
                if (mode != ProcessMode.Interaction)
                {
                    if (report.TryGetValue(row.NoteId, out var note))
                    {
                        if (note.Impressions is { } imp) row.Metrics["曝光"] = imp;
                        if (note.Reads is { } read) row.Metrics["阅读"] = read;
                        row.ReportDate = note.DataDate;
                    }
                    else reasons.Add(reportError.Length > 0 ? "蒲公英：" + reportError : "当前品牌报表未匹配笔记 ID");
                }
                var missing = record.Targets.Keys.Except(row.Metrics.Keys).ToArray();
                if (missing.Length > 0) reasons.Add("保留原值：" + string.Join("、", missing));
                row.Status = row.Metrics.Count == record.Targets.Count ? "成功" : row.Metrics.Count > 0 ? "部分更新" : "失败，保留原值";
                row.Reason = string.Join("；", reasons);
                foreach (var (name, value) in row.Metrics) numeric[record.Targets[name]] = value;
                results.Add(row); showRow(row);
                journal.WriteLine(JsonSerializer.Serialize(row));
                WriteLog($"第 {row.Row} 行 {row.Name}：{row.Status}；{row.Values}" + (row.Reason.Length > 0 ? "；" + row.Reason : "") +
                    $"；耗时 {rowClock.Elapsed.TotalSeconds:F2}s（解析 {resolveSeconds:F2}s，{route}）");
                if (results.Count % 10 == 0 || snapshotClock.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    WriteCsv(Path.Combine(directory, stem + "_回填明细.csv"), results);
                    AtomicJson(checkpoint, new Checkpoint(workbook.SourceHash, mode, started, results));
                    AtomicJson(Path.Combine(directory, "本次采集结果.json"), new Checkpoint(workbook.SourceHash, mode, started, results));
                    snapshotClock.Restart();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            WriteLog("已取消。正在保存已完成行；未处理行保持原值。");
        }
        catch (Exception ex)
        {
            interrupted = true;
            WriteLog("处理中断：" + ex.Message + "。正在保存已取得的结果。");
            throw;
        }
        finally
        {
            // Cancellation is deferred while final output is written, so a half-written workbook is never published.
            if (conversion.Count > 0) WriteCsv(Path.Combine(directory, stem + "_转换明细.csv"), conversion);
            if (results.Count > 0)
            {
                WriteCsv(Path.Combine(directory, stem + "_回填明细.csv"), results);
                AtomicJson(checkpoint, new Checkpoint(workbook.SourceHash, mode, started, results));
                AtomicJson(Path.Combine(directory, "本次采集结果.json"), new Checkpoint(workbook.SourceHash, mode, started, results));
            }
            if (numeric.Count > 0 || (mode == ProcessMode.Combined && links.Count > 0))
            {
                var suffix = cancelled || interrupted ? "_已完成部分.xlsx" : mode == ProcessMode.Combined ? "_短链与五项已处理.xlsx" : "_数据已更新.xlsx";
                // A combined run with no metrics produces only the clearly named link intermediate.
                if (numeric.Count > 0 || cancelled)
                {
                    var destination = Path.Combine(directory, stem + suffix);
                    await Task.Run(() => workbook.Save(destination, numeric, links));
                    hasWorkbook = true;
                    WriteLog("已保存：" + destination);
                }
            }
            else if (cancelled && links.Count > 0)
            {
                var destination = Path.Combine(directory, stem + "_已完成部分.xlsx");
                await Task.Run(() => workbook.Save(destination, [], links)); hasWorkbook = true;
            }
            WriteLog($"总耗时：{wholeRun.Elapsed.TotalSeconds:F2}s；结果目录：" + directory);
        }
        return Outcome(directory, results, cancelled, hasWorkbook, row => row.Status == "成功" && !conversion.Any(c => c.Row == row.Row && c.Status.StartsWith("失败")));
    }

    private static RowResult NewResult(NoteRecord record) => new() { Row = record.Row, Name = record.Name, SourceUrl = record.Url, ResultUrl = record.Url };
    private static ProcessingOutcome Outcome(string dir, List<RowResult> rows, bool cancelled, bool hasWorkbook, Func<RowResult, bool> complete) =>
        new(dir, rows.Count(complete), rows.Count(r => !complete(r) && r.Metrics.Count > 0), rows.Count(r => !complete(r) && r.Metrics.Count == 0), cancelled, hasWorkbook);
    private record Checkpoint(string Hash, ProcessMode Mode, DateTimeOffset Started, List<RowResult> Rows);
    private static void AtomicJson(string path, object value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
    private static void WriteCsv(string path, IEnumerable<RowResult> rows)
    {
        static string Quote(string value)
        {
            // Keep worksheet-originated strings from becoming formulas when the CSV is opened in Excel.
            if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var text = new StringBuilder("行号,达人,原链接,结果链接,笔记ID,状态,曝光,阅读,点赞,收藏,评论,互动采集时间,蒲公英数据日期,原因\r\n");
        foreach (var row in rows)
        {
            var fields = new[] { row.Row.ToString(), row.Name, row.SourceUrl, row.ResultUrl, row.NoteId, row.Status }
                .Concat(new[] { "曝光", "阅读", "点赞", "收藏", "评论" }.Select(k => row.Metrics.TryGetValue(k, out var n) ? n.ToString() : ""))
                .Concat([row.FetchedAt, row.ReportDate, row.Reason]);
            text.AppendLine(string.Join(",", fields.Select(Quote)));
        }
        File.WriteAllText(path + ".tmp", text.ToString(), new UTF8Encoding(true));
        File.Move(path + ".tmp", path, true);
    }
}
