namespace DmOffice.Dav;

/// <summary>
/// Farbmarkierung eines Fotos (Lightroom, Capture One, Bridge: xmp:Label; darktable: darktable:colorlabels; digiKam:
/// digiKam:ColorLabel). Bekannte Farben als Schlüssel (red, yellow, green, blue, purple, orange, pink, gray), auch aus
/// deutschen Namen („Rot“); eigene Namen aus Lightroom-Farbsätzen bleiben, wie sie sind.
/// </summary>
public static class ColorLabel
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = "red", ["rot"] = "red", ["yellow"] = "yellow", ["gelb"] = "yellow", ["green"] = "green", ["grün"] = "green", ["gruen"] = "green",
        ["blue"] = "blue", ["blau"] = "blue", ["purple"] = "purple", ["violet"] = "purple", ["violett"] = "purple", ["lila"] = "purple", ["magenta"] = "purple",
        ["orange"] = "orange", ["pink"] = "pink", ["rosa"] = "pink", ["gray"] = "gray", ["grey"] = "gray", ["grau"] = "gray",
    };
    // darktable: 0 rot, 1 gelb, 2 grün, 3 blau, 4 lila
    private static readonly string[] Darktable = ["red", "yellow", "green", "blue", "purple"];
    // digiKam: 1 rot, 2 orange, 3 gelb, 4 grün, 5 blau, 6 magenta, 7 grau (0 keine, 8 schwarz, 9 weiß)
    private static readonly string?[] DigiKam = [null, "red", "orange", "yellow", "green", "blue", "purple", "gray", null, null];

    /// <summary>Bekannte Farbe als Schlüssel, sonst der Name selbst (gekürzt); leer: null.</summary>
    public static string? Norm(string? label)
    {
        var l = label?.Trim();
        if (string.IsNullOrEmpty(l)) return null;
        return Names.TryGetValue(l, out var k) ? k : l.Length > 40 ? l[..40] : l;
    }

    /// <summary>Aus den XMP-Angaben, wie MetadataExtractor sie liefert (Schlüssel „xmp:Label“, „darktable:colorlabels[1]“ …).</summary>
    public static string? FromXmp(IDictionary<string, string> xmp)
    {
        if (xmp.TryGetValue("xmp:Label", out var l) && Norm(l) is { } n) return n;
        foreach (var (k, v) in xmp)
        {
            if (k.StartsWith("darktable:colorlabels[", StringComparison.Ordinal) && int.TryParse(v, out var d) && d >= 0 && d < Darktable.Length) return Darktable[d];
            if (k == "digiKam:ColorLabel" && int.TryParse(v, out var g) && g >= 0 && g < DigiKam.Length && DigiKam[g] is { } dk) return dk;
        }
        return null;
    }
}
