using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DmOffice.Gallery;

/// <summary>
/// XMP-Sidecar-Dateien neben Fotos (Lightroom, darktable, Capture One, digiKam): „IMG_1.ARW.xmp“ oder „IMG_1.xmp“.
/// Gelesen werden Titel, Beschreibung, Stichwörter, Bewertung, Farbmarkierung und Ort; sie gehen den Angaben im Foto selbst vor.
/// Geschrieben wird über <see cref="Dav.PhotoMeta"/>.
/// </summary>
public static class Sidecar
{
    public sealed record Data(string? Title, string? Description, List<string> Keywords, int? Rating, string? Label = null, double? Lat = null, double? Lon = null, int? Pick = null);

    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/", Xmp = "http://ns.adobe.com/xap/1.0/",
        Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#", Exif = "http://ns.adobe.com/exif/1.0/", XmpDM = "http://ns.adobe.com/xmp/1.0/DynamicMedia/";

    public const long MaxBytes = 2 * 1024 * 1024;

    public static bool Is(string name) => name.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Die Sidecar-Datei zu einem Foto aus den Namen eines Ordners (klein geschrieben): erst „foto.arw.xmp“, dann „foto.xmp“.</summary>
    public static string? For(string photo, IReadOnlyDictionary<string, string> sidecars) =>
        sidecars.TryGetValue(photo.ToLowerInvariant(), out var a) ? a
        : sidecars.TryGetValue(Path.GetFileNameWithoutExtension(photo).ToLowerInvariant(), out var b) ? b : null;

    /// <summary>Schlüssel einer Sidecar-Datei für <see cref="For"/>: ihr Name ohne „.xmp“, klein geschrieben.</summary>
    public static string Key(string sidecar) => sidecar[..^4].ToLowerInvariant();

    public static Data? Read(byte[] xml)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes * 2 };
            using var reader = XmlReader.Create(new MemoryStream(xml), settings);
            var doc = XDocument.Load(reader);
            string? Alt(XName n) => doc.Descendants(n).Descendants(Rdf + "li").Select(x => x.Value.Trim()).FirstOrDefault(v => v.Length > 0);
            var keywords = doc.Descendants(Dc + "subject").Descendants(Rdf + "li").Select(x => x.Value.Trim()).Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // xmp:Rating steht als Attribut an rdf:Description oder als eigenes Element
            var ratingText = doc.Descendants(Rdf + "Description").Select(d => (string?)d.Attribute(Xmp + "Rating")).FirstOrDefault(v => v != null)
                ?? doc.Descendants(Xmp + "Rating").Select(x => x.Value).FirstOrDefault();
            int? rating = int.TryParse(ratingText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) && r is >= 1 and <= 5 ? r : null;
            // Farbmarkierung: xmp:Label (Lightroom, Capture One), sonst darktable oder digiKam
            var labelText = doc.Descendants(Rdf + "Description").Select(d => (string?)d.Attribute(Xmp + "Label")).FirstOrDefault(v => v != null)
                ?? doc.Descendants(Xmp + "Label").Select(x => x.Value).FirstOrDefault();
            var label = Dav.ColorLabel.Norm(labelText);
            if (label == null)
            {
                var extra = new Dictionary<string, string>();
                var dt = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "colorlabels")?.Descendants(Rdf + "li").Select(x => x.Value.Trim()).FirstOrDefault();
                if (dt != null) extra["darktable:colorlabels[1]"] = dt;
                var dk = doc.Descendants(Rdf + "Description").Select(d => d.Attributes().FirstOrDefault(a => a.Name.LocalName == "ColorLabel")?.Value).FirstOrDefault(v => v != null)
                    ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ColorLabel")?.Value;
                if (dk != null) extra["digiKam:ColorLabel"] = dk;
                label = Dav.ColorLabel.FromXmp(extra);
            }
            // Ort: exif:GPSLatitude/-Longitude in Adobe-Schreibweise („48,14.531220N“), als Attribut oder Element
            string? Simple(XName n) => doc.Descendants(Rdf + "Description").Select(d => (string?)d.Attribute(n)).FirstOrDefault(v => v != null)
                ?? doc.Descendants(n).Select(x => x.Value).FirstOrDefault();
            var lat = Dav.PhotoMeta.ParseAdobeGps(Simple(Exif + "GPSLatitude"));
            var lon = Dav.PhotoMeta.ParseAdobeGps(Simple(Exif + "GPSLongitude"));
            if (lat == null || lon == null || Math.Abs(lat.Value) > 90) lat = lon = null;
            // Markierung wie Lightroom: xmpDM:pick 1 ausgewählt, -1 abgelehnt (0 ohne)
            var pickText = doc.Descendants(Rdf + "Description").Select(d => (string?)d.Attribute(XmpDM + "pick")).FirstOrDefault(v => v != null)
                ?? doc.Descendants(XmpDM + "pick").Select(x => x.Value).FirstOrDefault();
            int? pick = int.TryParse(pickText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pk) && pk != 0 ? Math.Sign(pk) : null;
            var data = new Data(Alt(Dc + "title"), Alt(Dc + "description"), keywords, rating, label, lat, lon, pick);
            return data.Title == null && data.Description == null && keywords.Count == 0 && rating == null && label == null && lat == null && pick == null ? null : data;
        }
        catch (Exception e) when (e is XmlException or InvalidOperationException) { return null; }
    }
}
