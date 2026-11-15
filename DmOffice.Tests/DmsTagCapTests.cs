using DmOffice.Dms;

namespace DmOffice.Tests;

/// <summary>Gelernte Tags: höchstens DmsAssign.MaxAutoTags, die wahrscheinlichsten; per Regel gesetzte zählen nicht mit.</summary>
public class DmsTagCapTests
{
    [Fact]
    public void Nur_die_drei_wahrscheinlichsten_gelernten_Tags_Regel_Tags_kommen_dazu()
    {
        var all = Enumerable.Range(1, 6).Select(i => new DmsEntry("t" + i, Dms.Dms.Tag, null, "Version 20" + (10 + i), null, Dms.Dms.Auto, null, false)).ToList();
        all.Add(new DmsEntry("rule", Dms.Dms.Tag, null, "Rechnung", null, "any", "rechnung", false));
        var probs = new Dictionary<string, double?> { ["t1"] = 0.90, ["t2"] = 0.99, ["t3"] = 0.95, ["t4"] = 0.97, ["t5"] = 0.86, ["t6"] = 0.50 };
        var r = DmsAssign.DecideWith(all, "Rechnung über ein Gerät", null, null, probs);
        Assert.Equal(["rule", "t2", "t3", "t4"], r.Tags.Order());
        Assert.Equal(3, DmsAssign.MaxAutoTags);
    }
}
