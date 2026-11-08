using DmOffice.Coach;

namespace DmOffice.Tests;

/// <summary>Fotos der Basisrezepte: jedes hat eins (eigenes oder das des gleichen Gerichts), und es ist ein WebP.</summary>
public class BaseRecipeImageTests
{
    [Fact]
    public void Jedes_Basisrezept_hat_ein_Foto()
    {
        Assert.Equal(210, BaseRecipes.All.Count);
        var missing = BaseRecipes.All.Where(r => r.Image == null).Select(r => r.Id).ToList();
        Assert.Empty(missing);
        foreach (var id in new[] { "base-dge-lunch-3", "base-vegetarian-lunch-3", "base-keto-dinner-2" })
        {
            var bytes = BaseRecipes.Image(id);
            Assert.NotNull(bytes);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes!, 0, 4));
        }
        Assert.Null(BaseRecipes.Image("base-gibt-es-nicht"));
    }
}
