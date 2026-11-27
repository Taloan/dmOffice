using System.Globalization;

namespace DmOffice.Core;

/// <summary>Zahlen in Texten für Menschen (Aktivität, Protokoll, Meldungen): mit deutschem Tausenderpunkt, unabhängig von der Kultur des Servers.</summary>
public static class Num
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    public static string N(long value) => value.ToString("N0", De);
    public static string N(double value) => value.ToString("N0", De);
    public static string N(decimal value) => value.ToString("N0", De);
}
