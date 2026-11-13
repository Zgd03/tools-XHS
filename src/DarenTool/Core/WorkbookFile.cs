using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DarenTool.Core;

/// <summary>Edits only the target worksheet parts and calculation metadata; other ZIP entries retain their content.</summary>
public sealed partial class WorkbookFile
{
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private readonly string source;
    private readonly string sheetPart;
    private readonly string relPart;
    private readonly string sourceHash;
    private readonly XDocument sheet;
    private readonly XDocument relations;
    private readonly Dictionary<string, XElement> cells;
    private readonly Dictionary<string, string> links;
    private readonly Dictionary<string, string> values;
    public List<NoteRecord> Records { get; }
    public string SourceHash => sourceHash;

    public WorkbookFile(string path, ProcessMode mode)
    {
        source = Path.GetFullPath(path);
        if (!File.Exists(source) || !source.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择存在的 .xlsx 文件。暂不支持 .xls、加密文件或 CSV。");
        sourceHash = Hash(source);
        using var zip = ZipFile.OpenRead(source);
        var workbook = Load(zip, "xl/workbook.xml");
        var bookRels = Load(zip, "xl/_rels/workbook.xml.rels");
        var target = workbook.Descendants(M + "sheet").SingleOrDefault(x => (string?)x.Attribute("name") == "达人数据更新")
            ?? throw new InvalidOperationException("找不到“达人数据更新”工作表。");
        var relation = bookRels.Root!.Elements().Single(x => (string?)x.Attribute("Id") == (string?)target.Attribute(R + "id"));
        sheetPart = Resolve("xl/workbook.xml", (string)relation.Attribute("Target")!);
        relPart = Path.GetDirectoryName(sheetPart)!.Replace('\\', '/') + "/_rels/" + Path.GetFileName(sheetPart) + ".rels";
        sheet = Load(zip, sheetPart);
        relations = zip.GetEntry(relPart) is not null ? Load(zip, relPart) : new XDocument(new XElement(P + "Relationships"));
        var strings = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Load(zip, "xl/sharedStrings.xml").Root!.Elements(M + "si")
            .Select(x => string.Concat(x.Descendants(M + "t").Select(t => t.Value))).ToArray();
        cells = sheet.Descendants(M + "sheetData").Elements(M + "row").Elements(M + "c").ToDictionary(x => (string)x.Attribute("r")!);
        values = cells.ToDictionary(x => x.Key, x => ReadValue(x.Value, strings));
        var relMap = relations.Root!.Elements().ToDictionary(x => (string)x.Attribute("Id")!, x => (string?)x.Attribute("Target") ?? "");
        links = sheet.Descendants(M + "hyperlink").Where(x => x.Attribute("ref") is not null && x.Attribute(R + "id") is not null)
            .ToDictionary(x => (string)x.Attribute("ref")!, x => relMap.GetValueOrDefault((string)x.Attribute(R + "id")!) ?? "");
        Records = FindRecords(mode);
        if (Records.Count == 0) throw new InvalidOperationException("工作表中没有可处理的发布链接。数据更新时还需要对应行填写“达人”。");
    }

    private List<NoteRecord> FindRecords(ProcessMode mode)
    {
        var required = mode switch
        {
            ProcessMode.Shorten => new[] { "发布链接" },
            ProcessMode.Interaction => ["达人", "发布链接", "点赞", "收藏", "评论"],
            _ => ["达人", "发布链接", "曝光", "阅读", "点赞", "收藏", "评论"]
        };
        Dictionary<string, string>? columns = null;
        var header = 0;
        for (var row = 1; row <= 30; row++)
        {
            var candidates = values.Where(x => Row(x.Key) == row && required.Contains(x.Value.Trim())).ToList();
            if (!required.All(name => candidates.Any(x => x.Value.Trim() == name))) continue;
            if (candidates.GroupBy(x => x.Value.Trim()).Any(g => g.Count() > 1)) throw new InvalidOperationException($"第 {row} 行存在重复的必要列名，请确认表头。");
            columns = candidates.ToDictionary(x => x.Value.Trim(), x => Column(x.Key)); header = row; break;
        }
        if (columns is null) throw new InvalidOperationException("前 30 行未找到所需表头：" + string.Join("、", required));
        var records = new List<NoteRecord>();
        foreach (var address in values.Keys.Concat(links.Keys).Distinct().Where(a => Column(a) == columns["发布链接"] && Row(a) > header).OrderBy(Row))
        {
            var display = values.GetValueOrDefault(address, "").Trim();
            var url = links.GetValueOrDefault(address, display).Trim();
            // Repair stale hyperlink targets using the visible original long URL, as in the previous tool.
            if (mode is ProcessMode.Shorten or ProcessMode.Combined && NoteLinks.Id(display).Length > 0) url = display;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            var row = Row(address);
            var name = columns.TryGetValue("达人", out var nc) ? values.GetValueOrDefault(nc + row, "").Trim() : "";
            if (mode != ProcessMode.Shorten && name.Length == 0) continue;
            var targets = required.Except(["达人", "发布链接"]).ToDictionary(k => k, k => columns[k] + row);
            if (targets.Values.Any(a => cells.TryGetValue(a, out var c) && c.Element(M + "f") is not null))
                throw new InvalidOperationException($"第 {row} 行的目标指标含公式，已停止，避免覆盖公式。");
            records.Add(new(row, name, url, address, targets));
        }
        return records;
    }

    public bool LinkHasFormula(string address) => cells.TryGetValue(address, out var c) && c.Element(M + "f") is not null;

    public void Save(string destination, Dictionary<string, long> numbers, Dictionary<string, string> urls)
    {
        if (Path.GetFullPath(destination).Equals(source, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能覆盖原文件。");
        if (Hash(source) != sourceHash) throw new InvalidOperationException("处理期间源文件发生变化，未写入。请恢复原文件后重新处理。");
        var changedSheet = new XDocument(sheet);
        var changedRels = new XDocument(relations);
        var data = changedSheet.Root!.Element(M + "sheetData")!;
        foreach (var (address, value) in numbers) WriteCell(data, address, value.ToString(System.Globalization.CultureInfo.InvariantCulture), false);
        foreach (var (address, url) in urls)
        {
            if (!NoteLinks.IsShort(url)) throw new InvalidOperationException("拒绝写入非官方短链接。");
            WriteCell(data, address, url, true);
            var root = changedSheet.Root!;
            var hyperlinks = root.Element(M + "hyperlinks");
            if (hyperlinks is null)
            {
                hyperlinks = new XElement(M + "hyperlinks");
                var later = new HashSet<string> { "printOptions", "pageMargins", "pageSetup", "headerFooter", "rowBreaks", "colBreaks", "customProperties", "cellWatches", "ignoredErrors", "smartTags", "drawing", "legacyDrawing", "legacyDrawingHF", "picture", "oleObjects", "controls", "webPublishItems", "tableParts", "extLst" };
                var anchor = root.Elements().FirstOrDefault(x => later.Contains(x.Name.LocalName));
                if (anchor is null) root.Add(hyperlinks); else anchor.AddBeforeSelf(hyperlinks);
            }
            var used = changedRels.Root!.Elements().Select(x => (string?)x.Attribute("Id")).ToHashSet();
            var index = 1;
            while (used.Contains("rIdDaren" + index)) index++;
            var id = "rIdDaren" + index;
            changedRels.Root!.Add(new XElement(P + "Relationship", new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/hyperlink"), new XAttribute("Target", url), new XAttribute("TargetMode", "External")));
            hyperlinks.Elements(M + "hyperlink").Where(x => (string?)x.Attribute("ref") == address).Remove();
            hyperlinks.Add(new XElement(M + "hyperlink", new XAttribute("ref", address), new XAttribute(R + "id", id)));
        }
        var patches = new Dictionary<string, byte[]> { [sheetPart] = Bytes(changedSheet) };
        if (urls.Count > 0) patches[relPart] = Bytes(changedRels);
        using var input = ZipFile.OpenRead(source);
        if (numbers.Count > 0)
        {
            var wb = Load(input, "xl/workbook.xml");
            var calc = wb.Root!.Element(M + "calcPr");
            if (calc is null)
            {
                calc = new XElement(M + "calcPr");
                var anchor = wb.Root.Elements().FirstOrDefault(x => new[] { "oleSize", "customWorkbookViews", "pivotCaches", "smartTagPr", "smartTagTypes", "webPublishing", "fileRecoveryPr", "webPublishObjects", "extLst" }.Contains(x.Name.LocalName));
                if (anchor is null) wb.Root.Add(calc); else anchor.AddBeforeSelf(calc);
            }
            calc.SetAttributeValue("fullCalcOnLoad", "1"); calc.SetAttributeValue("forceFullCalc", "1"); calc.SetAttributeValue("calcMode", "auto");
            patches["xl/workbook.xml"] = Bytes(wb);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var entry in input.Entries)
                {
                    var copy = output.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    copy.LastWriteTime = entry.LastWriteTime; copy.ExternalAttributes = entry.ExternalAttributes;
                    using var stream = copy.Open();
                    if (patches.TryGetValue(entry.FullName, out var bytes)) stream.Write(bytes);
                    else { using var original = entry.Open(); original.CopyTo(stream); }
                }
                foreach (var (name, bytes) in patches.Where(p => input.GetEntry(p.Key) is null))
                { using var stream = output.CreateEntry(name, CompressionLevel.Optimal).Open(); stream.Write(bytes); }
            }
            if (Hash(source) != sourceHash) throw new InvalidOperationException("源文件发生变化，已停止导出。");
            File.Move(temp, destination, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void WriteCell(XElement data, string address, string value, bool text)
    {
        var rowNum = Row(address);
        var row = data.Elements(M + "row").FirstOrDefault(x => (int?)x.Attribute("r") == rowNum);
        if (row is null)
        {
            row = new XElement(M + "row", new XAttribute("r", rowNum));
            var next = data.Elements(M + "row").FirstOrDefault(x => (int?)x.Attribute("r") > rowNum);
            if (next is null) data.Add(row); else next.AddBeforeSelf(row);
        }
        var cell = row.Elements(M + "c").FirstOrDefault(x => (string?)x.Attribute("r") == address);
        if (cell is null)
        {
            cell = new XElement(M + "c", new XAttribute("r", address));
            var next = row.Elements(M + "c").FirstOrDefault(x => ColumnNumber((string)x.Attribute("r")!) > ColumnNumber(address));
            if (next is null) row.Add(cell); else next.AddBeforeSelf(cell);
        }
        if (cell.Element(M + "f") is not null) throw new InvalidOperationException($"{address} 含公式，拒绝覆盖。");
        cell.Elements(M + "v").Remove(); cell.Elements(M + "is").Remove(); cell.SetAttributeValue("t", text ? "inlineStr" : null);
        var content = text ? new XElement(M + "is", new XElement(M + "t", value)) : new XElement(M + "v", value);
        var extension = cell.Element(M + "extLst");
        if (extension is null) cell.Add(content); else extension.AddBeforeSelf(content);
    }
    public static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
    private static int Row(string address) => int.Parse(Digits().Match(address).Value);
    private static string Column(string address) => Digits().Replace(address, "");
    private static int ColumnNumber(string address) { var n = 0; foreach (var ch in Column(address)) n = n * 26 + ch - 'A' + 1; return n; }
    private static string ReadValue(XElement c, string[] strings)
    {
        var value = c.Element(M + "v")?.Value ?? "";
        return (string?)c.Attribute("t") switch { "s" => strings[int.Parse(value)], "inlineStr" => string.Concat(c.Descendants(M + "t").Select(x => x.Value)), _ => value };
    }
    private static string Resolve(string basePart, string target) => new Uri(new Uri("https://xlsx.local/" + basePart), target.Replace('\\', '/')).AbsolutePath.TrimStart('/');
    private static XDocument Load(ZipArchive zip, string part)
    {
        var entry = zip.GetEntry(part) ?? throw new InvalidOperationException("工作簿缺少必要内容：" + part);
        if (entry.Length > 128 * 1024 * 1024) throw new InvalidOperationException("工作簿单个部件过大，已停止读取。");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private static byte[] Bytes(XDocument doc)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false })) doc.Save(writer);
        return stream.ToArray();
    }
    [GeneratedRegex(@"\d+$")] private static partial Regex Digits();
}
