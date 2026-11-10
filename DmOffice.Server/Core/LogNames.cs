namespace DmOffice.Core;

/// <summary>
/// Wie Konten im Server-Protokoll heißen: Anmeldename und Nummer des Kontos, nie die Adresse. Das Protokoll liest der Admin -
/// Adressen, Empfänger, Ordner- und Listennamen gehören nicht hinein.
/// </summary>
public static class LogNames
{
    public static string Of(UserSpace space, MailAccount account) =>
        $"{space.User.Login}/Konto {space.Settings.MailAccounts.FindIndex(a => a.Id == account.Id) + 1}";
}
