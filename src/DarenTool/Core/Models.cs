using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DarenTool.Core;

public enum ProcessMode { Five, Interaction, Shorten, Combined }
public record NoteRecord(int Row, string Name, string Url, string LinkCell, Dictionary<string, string> Targets);
public record ReportNote(string Id, string BrandId, string Brand, long? Impressions, long? Reads, string DataDate);
public sealed record RowResult
{
    public int Row { get; init; }
    public string Name { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string ResultUrl { get; set; } = "";
    public string NoteId { get; set; } = "";
    public string Status { get; set; } = "待处理";
    public string Reason { get; set; } = "";
    public string ReportDate { get; set; } = "";
    public string FetchedAt { get; set; } = "";
    public Dictionary<string, long> Metrics { get; set; } = [];
    public string Values => string.Join("，", Metrics.Select(x => $"{x.Key} {x.Value}"));
}

public static partial class NoteLinks
{
    public static bool IsShort(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Scheme is "http" or "https" && u.UserInfo.Length == 0 && u.IsDefaultPort &&
        u.Host is "xhslink.com" or "www.xhslink.com" or "xhslink.cn" or "www.xhslink.cn" && u.AbsolutePath.Length > 1;
    public static bool Allowed(Uri uri) => uri.Scheme is "http" or "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host is "xiaohongshu.com" or "www.xiaohongshu.com" || IsShort(uri.AbsoluteUri));
    public static string Id(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !Allowed(u)) return "";
        var m = NotePath().Match(u.AbsolutePath);
        return u.Host is "xiaohongshu.com" or "www.xiaohongshu.com" && m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
    }
    public static long? Count(JsonNode? node, bool approximate = true)
    {
        if (node is null) return null;
        var text = node.ToString().Trim().Replace(",", "");
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0) return n;
        var m = CountText().Match(text);
        if (approximate && m.Success && decimal.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
        {
            var multiplier = m.Groups[2].Value.ToLowerInvariant() switch { "亿" => 100000000m, "k" => 1000m, _ => 10000m };
            if (d * multiplier <= long.MaxValue) return (long)(d * multiplier);
        }
        return null;
    }
    [GeneratedRegex(@"^/(?:explore|discovery/item)/([0-9a-fA-F]{24})/?$")]
    private static partial Regex NotePath();
    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(万|亿|[wWkK])$")]
    private static partial Regex CountText();
}
