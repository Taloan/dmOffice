using System.Text;
using DmOffice.Dav;
using DmOffice.Gallery;

namespace DmOffice.Tests;

/// <summary>Farbmarkierung aus Lightroom, Capture One, darktable und digiKam - im Foto (XMP) und in Sidecar-Dateien.</summary>
public class ColorLabelTests
{
    [Theory]
    [InlineData("Red", "red")]
    [InlineData("Rot", "red")]
    [InlineData("grün", "green")]
    [InlineData("Violett", "purple")]
    [InlineData("Zu prüfen", "Zu prüfen")]
    [InlineData("  ", null)]
    public void Namen_werden_zu_Schluesseln(string input, string? expected) => Assert.Equal(expected, ColorLabel.Norm(input));

    [Fact]
    public void Darktable_und_digiKam()
    {
        Assert.Equal("blue", ColorLabel.FromXmp(new Dictionary<string, string> { ["darktable:colorlabels[1]"] = "3" }));
        Assert.Equal("orange", ColorLabel.FromXmp(new Dictionary<string, string> { ["digiKam:ColorLabel"] = "2" }));
        Assert.Null(ColorLabel.FromXmp(new Dictionary<string, string> { ["digiKam:ColorLabel"] = "0" }));
    }

    [Fact]
    public void Sidecar_von_Lightroom_mit_Label_und_Bewertung()
    {
        var xml = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:Rating="4" xmp:Label="Gelb"/>
            </rdf:RDF></x:xmpmeta>
            """;
        var d = Sidecar.Read(Encoding.UTF8.GetBytes(xml));
        Assert.NotNull(d);
        Assert.Equal(4, d!.Rating);
        Assert.Equal("yellow", d.Label);
    }

    [Fact]
    public void Sidecar_von_darktable_nur_mit_Farbe()
    {
        var xml = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:darktable="http://darktable.sf.net/">
                <darktable:colorlabels><rdf:Seq><rdf:li>2</rdf:li></rdf:Seq></darktable:colorlabels>
              </rdf:Description>
            </rdf:RDF></x:xmpmeta>
            """;
        Assert.Equal("green", Sidecar.Read(Encoding.UTF8.GetBytes(xml))?.Label);
    }
}
