using System.IO.Compression;
using System.Text;

namespace DmOffice.Office;

/// <summary>
/// Leere Office-Dokumente fuer "Neu → Word-/Excel-/PowerPoint-Dokument": kleinste gueltige OOXML-Pakete (A4, eine leere
/// Tabelle, eine leere Folie 16:9). Hier erzeugt statt als Datei mitgeliefert - so bleibt nichts Fremdes im Repo.
/// </summary>
public static class OfficeTemplates
{
    public static readonly string[] Kinds = ["docx", "xlsx", "pptx"];

    public static byte[] Create(string ext) => ext.TrimStart('.').ToLowerInvariant() switch
    {
        "docx" => Zip(Docx),
        "xlsx" => Zip(Xlsx),
        "pptx" => Zip(Pptx),
        _ => throw new ArgumentException("Unbekannte Dokumentart."),
    };

    private static byte[] Zip((string Name, string Xml)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in parts)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var s = e.Open();
                s.Write(Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" + xml));
            }
        }
        return ms.ToArray();
    }

    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    private static string Types(params (string Part, string Type)[] overrides) =>
        $"<Types xmlns=\"{Ct}\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
        + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
        + string.Concat(overrides.Select(o => $"<Override PartName=\"{o.Part}\" ContentType=\"{o.Type}\"/>")) + "</Types>";

    private static string Rels(params (string Id, string Type, string Target)[] rels) =>
        $"<Relationships xmlns=\"{PkgRel}\">" + string.Concat(rels.Select(r => $"<Relationship Id=\"{r.Id}\" Type=\"{Rel}/{r.Type}\" Target=\"{r.Target}\"/>")) + "</Relationships>";

    private static readonly (string, string)[] Docx =
    [
        ("[Content_Types].xml", Types(("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"))),
        ("_rels/.rels", Rels(("rId1", "officeDocument", "word/document.xml"))),
        ("word/document.xml",
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p/>"
            + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1417\" w:right=\"1417\" w:bottom=\"1134\" w:left=\"1417\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>"
            + "</w:body></w:document>"),
    ];

    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly (string, string)[] Xlsx =
    [
        ("[Content_Types].xml", Types(
            ("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
            ("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"),
            ("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))),
        ("_rels/.rels", Rels(("rId1", "officeDocument", "xl/workbook.xml"))),
        ("xl/workbook.xml", $"<workbook xmlns=\"{S}\" xmlns:r=\"{Rel}\"><sheets><sheet name=\"Tabelle1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels", Rels(("rId1", "worksheet", "worksheets/sheet1.xml"), ("rId2", "styles", "styles.xml"))),
        ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{S}\"><sheetData/></worksheet>"),
        ("xl/styles.xml",
            $"<styleSheet xmlns=\"{S}\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts>"
            + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
            + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
            + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>"
            + "<cellStyles count=\"1\"><cellStyle name=\"Standard\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>"),
    ];

    private const string EmptyTree =
        "<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
        + "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree></p:cSld>";

    private static string Ns => $"xmlns:a=\"{A}\" xmlns:r=\"{Rel}\" xmlns:p=\"{P}\"";

    private static string Solid(string color) => $"<a:solidFill><a:schemeClr val=\"{color}\"/></a:solidFill>";

    private static readonly string Theme =
        $"<a:theme xmlns:a=\"{A}\" name=\"Office\"><a:themeElements>"
        + "<a:clrScheme name=\"Office\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>"
        + "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2><a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1>"
        + "<a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>"
        + "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink>"
        + "<a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>"
        + "<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>"
        + "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>"
        + "<a:fmtScheme name=\"Office\"><a:fillStyleLst>" + Solid("phClr") + Solid("phClr") + Solid("phClr") + "</a:fillStyleLst>"
        + "<a:lnStyleLst><a:ln w=\"6350\">" + Solid("phClr") + "</a:ln><a:ln w=\"12700\">" + Solid("phClr") + "</a:ln><a:ln w=\"19050\">" + Solid("phClr") + "</a:ln></a:lnStyleLst>"
        + "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>"
        + "<a:bgFillStyleLst>" + Solid("phClr") + Solid("phClr") + Solid("phClr") + "</a:bgFillStyleLst></a:fmtScheme>"
        + "</a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";

    private static readonly (string, string)[] Pptx =
    [
        ("[Content_Types].xml", Types(
            ("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"),
            ("/ppt/slideMasters/slideMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"),
            ("/ppt/slideLayouts/slideLayout1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"),
            ("/ppt/slides/slide1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"),
            ("/ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml"))),
        ("_rels/.rels", Rels(("rId1", "officeDocument", "ppt/presentation.xml"))),
        ("ppt/presentation.xml",
            $"<p:presentation {Ns}><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>"
            + "<p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/></p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>"),
        ("ppt/_rels/presentation.xml.rels", Rels(("rId1", "slideMaster", "slideMasters/slideMaster1.xml"), ("rId2", "slide", "slides/slide1.xml"), ("rId3", "theme", "theme/theme1.xml"))),
        ("ppt/slideMasters/slideMaster1.xml",
            $"<p:sldMaster {Ns}>{EmptyTree}"
            + "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>"
            + "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst></p:sldMaster>"),
        ("ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", "theme", "../theme/theme1.xml"))),
        ("ppt/slideLayouts/slideLayout1.xml", $"<p:sldLayout {Ns} type=\"blank\" preserve=\"1\">{EmptyTree.Replace("<p:cSld>", "<p:cSld name=\"Leer\">")}<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>"),
        ("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Rels(("rId1", "slideMaster", "../slideMasters/slideMaster1.xml"))),
        ("ppt/slides/slide1.xml", $"<p:sld {Ns}>{EmptyTree}<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>"),
        ("ppt/slides/_rels/slide1.xml.rels", Rels(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"))),
        ("ppt/theme/theme1.xml", Theme),
    ];
}
