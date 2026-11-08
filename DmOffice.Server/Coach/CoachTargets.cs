namespace DmOffice.Coach;

/// <summary>Profil für die Berechnung (kv „coach.profile“). Alles optional - ohne Geburtsdatum, Geschlecht, Größe, Gewicht keine Kalorienziele.</summary>
public sealed class CoachProfile
{
    public DateOnly? Birth { get; set; }
    /// <summary>f, m oder d (divers: Mittelwert der Formeln)</summary>
    public string? Sex { get; set; }
    public double? HeightCm { get; set; }
    /// <summary>Startgewicht; das zuletzt eingetragene Gewicht (coach_body) hat Vorrang.</summary>
    public double? WeightKg { get; set; }
    /// <summary>Alltagsaktivität als PAL: 1.2, 1.4, 1.6, 1.8, 2.0</summary>
    public double Pal { get; set; } = 1.4;
    /// <summary>keep, lose, gain, muscle</summary>
    public string Goal { get; set; } = "keep";
    /// <summary>Tempo in kg pro Woche (abnehmen/zunehmen): 0.25, 0.5, 0.75</summary>
    public double Pace { get; set; } = 0.5;
    /// <summary>dge, med, vegetarian, vegan, protein, lowcarb, keto</summary>
    public string Diet { get; set; } = "dge";
    /// <summary>EU-Hauptallergene (gluten, crustaceans, eggs, fish, peanuts, soy, milk, nuts, celery, mustard, sesame, sulphites, lupin, molluscs)</summary>
    public List<string> Allergens { get; set; } = [];
    /// <summary>lactose, fructose, histamine, gluten</summary>
    public List<string> Intolerances { get; set; } = [];
    public List<string> Dislikes { get; set; } = [];
    public bool Pregnant { get; set; }
    /// <summary>fixed oder dynamic</summary>
    public string StepMode { get; set; } = "fixed";
    public int StepGoal { get; set; } = 7000;
    /// <summary>Eigenes Trinkziel in ml (null: berechnet)</summary>
    public int? WaterMl { get; set; }
    /// <summary>Eigene Gläser und Flaschen in ml (höchstens vier): die erste bestimmt die Symbole der Wasser-Karte, jede wird ein „+“-Knopf.</summary>
    public List<int> WaterSizes { get; set; } = [250, 500];
    /// <summary>Zahlen (kcal) ausblenden - nur Verlauf und Hinweise</summary>
    public bool HideNumbers { get; set; }
    /// <summary>Pukkis KI darf eigene Coach-Werte (Ziele, heute Gegessenes) als Grundlage bekommen - aus bis eingeschaltet</summary>
    public bool AiData { get; set; }
    /// <summary>Pukki als Coach: erinnert ans Trinken und an Bewegung, sonntags Wochenrückblick - aus bis eingeschaltet</summary>
    public bool Nudges { get; set; }
    public bool NudgeWater { get; set; } = true;
    public bool NudgeMove { get; set; } = true;
    public bool NudgeWeek { get; set; } = true;
}

/// <summary>Tagesziele samt Rechenweg und Hinweisen. Kcal null: Profil unvollständig.</summary>
public sealed record CoachTargets(
    double? Bmr, double? Tdee, double? Kcal, double? Protein, double? Carbs, double? Fat, double Fiber, int WaterMl,
    int? Age, double? Bmi, string Goal, string Diet, List<string> Notes, bool Complete);

/// <summary>
/// Empfehlungen nach wissenschaftlichen Grundlagen (docs/dmcoach-konzept.md): Grundumsatz nach Mifflin-St Jeor, Gesamtbedarf mit
/// PAL (DGE), Ziel mit Leitplanken, Makros je Ernährungsform, Wasser (35 ml/kg, davon gut die Hälfte aus Getränken, mindestens
/// 1,5 l nach DGE). Keine Diagnosen; die Leitplanken verhindern zu niedrige Ziele.
/// </summary>
public static class CoachTargetsCalc
{
    public static int? AgeOf(DateOnly? birth, DateOnly today)
    {
        if (birth is not { } b) return null;
        var age = today.Year - b.Year;
        if (today < b.AddYears(age)) age--;
        return age;
    }

    public static double? Bmr(CoachProfile p, double? weight, int? age)
    {
        if (weight is not double kg || p.HeightCm is not double cm || age is not int a || p.Sex is not ("f" or "m" or "d")) return null;
        var basis = 10 * kg + 6.25 * cm - 5 * a;
        return p.Sex switch { "m" => basis + 5, "f" => basis - 161, _ => basis - 78 };
    }

    public static CoachTargets Compute(CoachProfile p, double? weight, DateOnly today)
    {
        var notes = new List<string>();
        var age = AgeOf(p.Birth, today);
        weight ??= p.WeightKg;
        double? bmi = weight is double w && p.HeightCm is double h && h > 0 ? Math.Round(w / Math.Pow(h / 100, 2), 1) : null;
        var water = WaterOf(p, weight, age);
        var bmr = Bmr(p, weight, age);
        if (bmr == null)
            return new CoachTargets(null, null, null, null, null, null, 30, water, age, bmi, p.Goal, p.Diet,
                ["Für Kalorienziele fehlen noch Geburtsdatum, Geschlecht, Größe oder Gewicht."], false);

        var pal = Math.Clamp(p.Pal, 1.2, 2.4);
        var tdee = bmr.Value * pal;
        var goal = p.Goal;
        // Leitplanken: kein Abnehmen bei Untergewicht, Minderjährigen, Schwangerschaft/Stillzeit
        if (goal == "lose" && (bmi < 18.5 || age < 18 || p.Pregnant))
        {
            notes.Add(p.Pregnant ? "In Schwangerschaft und Stillzeit gibt es kein Abnehmziel – ausgewogen essen, im Zweifel ärztlich beraten lassen."
                : age < 18 ? "Unter 18 Jahren gibt es kein Abnehmziel – ausgewogen essen, im Zweifel ärztlich beraten lassen."
                : "Bei einem BMI unter 18,5 gibt es kein Abnehmziel. Wenn du dir Sorgen um dein Gewicht machst, sprich mit einer Ärztin oder einem Arzt.");
            goal = "keep";
        }
        var kcal = tdee;
        if (goal == "lose")
        {
            // Tempo höchstens 1 % des Körpergewichts pro Woche; Defizit höchstens 25 % bzw. 750 kcal
            var pace = Math.Min(Math.Clamp(p.Pace, 0.1, 1.0), weight!.Value * 0.01);
            var deficit = Math.Min(Math.Min(pace * 7700 / 7, tdee * 0.25), 750);
            kcal = tdee - deficit;
            var floor = Math.Max(bmr.Value, p.Sex == "m" ? 1500 : p.Sex == "f" ? 1200 : 1350);
            if (kcal < floor) { kcal = floor; notes.Add("Das Ziel liegt nicht unter deinem Grundumsatz – langsamer abnehmen ist gesünder."); }
        }
        else if (goal == "gain") kcal = tdee + 250;
        else if (goal == "muscle") kcal = tdee + 300;
        kcal = Math.Round(kcal / 10) * 10;

        // Makros (Anteil an der Energie), Eiweiß wo sinnvoll je kg
        var kg = weight!.Value;
        double protein, fat, carbs;
        switch (p.Diet)
        {
            case "med": carbs = 0.45; fat = 0.37; protein = 0.18; break;
            case "protein": protein = Math.Min(1.8 * kg * 4 / kcal, 0.35); fat = 0.30; carbs = 1 - protein - fat; break;
            case "lowcarb": carbs = 0.20; protein = 0.25; fat = 0.55; break;
            case "keto":
                carbs = Math.Min(0.08, 50.0 * 4 / kcal); protein = 0.20; fat = 1 - carbs - protein;
                notes.Add("Keto ist nicht geeignet bei Diabetes mit Insulin, in Schwangerschaft und Stillzeit sowie bei Nieren- oder Lebererkrankungen ohne ärztliche Begleitung.");
                break;
            default: protein = 0.15; fat = 0.30; carbs = 0.55; break;   // DGE, vegetarisch, vegan
        }
        // DGE: mindestens 0,8 g/kg (ab 65: 1,0); Muskelaufbau: 1,6 g/kg
        var minProtein = (goal == "muscle" ? 1.6 : age >= 65 ? 1.0 : 0.8) * kg * 4 / kcal;
        if (protein < minProtein) { var add = minProtein - protein; protein = minProtein; if (p.Diet == "keto") fat -= add; else carbs = Math.Max(0.05, carbs - add); }
        if (p.Diet == "vegan") notes.Add("Vegan: Vitamin B12 musst du ergänzen; achte auf Eiweißquellen, Eisen, Jod, Calcium und Omega-3.");
        return new CoachTargets(Math.Round(bmr.Value), Math.Round(tdee), kcal, Math.Round(kcal * protein / 4), Math.Round(kcal * carbs / 4),
            Math.Round(kcal * fat / 9), 30, water, age, bmi, goal, p.Diet, notes, true);
    }

    /// <summary>Trinkziel: 35 ml/kg (ab 65: 30) gesamt, davon 55 % aus Getränken, mindestens 1,5 l (DGE); auf 50 ml gerundet.</summary>
    public static int WaterOf(CoachProfile p, double? weight, int? age)
    {
        if (p.WaterMl is int own && own >= 500 && own <= 6000) return own;
        if (weight is not double kg) return 1500;
        var ml = kg * (age >= 65 ? 30 : 35) * 0.55;
        return (int)(Math.Round(Math.Max(1500, ml) / 50) * 50);
    }

    /// <summary>
    /// Dynamisches Schrittziel: beginnt bei 10.000, bewegt sich jeden Tag um höchstens 10 % auf den Durchschnitt der letzten
    /// drei Tage mit Daten zu; zwischen 3.000 und 20.000.
    /// </summary>
    public static int NextStepGoal(int previousGoal, IReadOnlyList<int> lastDays)
    {
        if (lastDays.Count == 0) return previousGoal;
        var avg = lastDays.Average();
        var step = Math.Clamp(avg - previousGoal, -0.1 * previousGoal, 0.1 * previousGoal);
        return (int)Math.Clamp(Math.Round((previousGoal + step) / 10) * 10, 3000, 20000);
    }
}
