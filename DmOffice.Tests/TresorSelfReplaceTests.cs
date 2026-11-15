using DmOffice;
using Xunit;

namespace DmOffice.Tests;

/// <summary>„Ersetzen“ im Tresor: nie die Quelle selbst oder einen Ordner, der sie enthält.</summary>
public class TresorSelfReplaceTests
{
    [Theory]
    [InlineData("Fotos/a.jpg", "Fotos/a.jpg", true)]
    [InlineData("Fotos", "Fotos/Fotos", true)]
    [InlineData("", "Fotos/a.jpg", true)]
    [InlineData("Fotos/b.jpg", "Fotos/a.jpg", false)]
    [InlineData("Foto", "Fotos/a.jpg", false)]
    [InlineData("Fotos/Fotos", "Fotos", false)]
    public void Ziel_gleich_Quelle_oder_darueber(string target, string source, bool self) =>
        Assert.Equal(self, TresorPaths.SelfOrAncestor(target, source));
}
