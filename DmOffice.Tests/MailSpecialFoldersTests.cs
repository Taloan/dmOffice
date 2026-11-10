using DmOffice.Mail;
using MailKit;

namespace DmOffice.Tests;

/// <summary>Sonderordner: vom Server gekennzeichnete gehen vor gleichnamigen Ordnern anderer Programme (Gmail).</summary>
public class MailSpecialFoldersTests
{
    [Fact]
    public void Gmail_gekennzeichnete_Ordner_gehen_vor_gleichnamigen_Labels()
    {
        var folders = new List<(string, string, string?, FolderAttributes)>
        {
            ("INBOX", "INBOX", null, FolderAttributes.None),
            ("[Gmail]/Gesendet", "Gesendet", "[Gmail]", FolderAttributes.Sent),
            ("Gesendete Elemente", "Gesendete Elemente", null, FolderAttributes.None),
            ("[Gmail]/Spam", "Spam", "[Gmail]", FolderAttributes.Junk),
            ("Junk", "Junk", null, FolderAttributes.None),
            ("Junk-E-Mail", "Junk-E-Mail", null, FolderAttributes.None),
            ("[Gmail]/Entwürfe", "Entwürfe", "[Gmail]", FolderAttributes.Drafts),
            ("Entwürfe", "Entwürfe", null, FolderAttributes.None),
            ("Bestellung", "Bestellung", null, FolderAttributes.None),
        };
        var s = MailSync.AssignSpecials(folders);
        Assert.Equal(new string?[] { "inbox", "sent", null, "junk", null, null, "drafts", null, null }, s);
    }

    [Fact]
    public void Ohne_Kennzeichnung_je_Art_nur_ein_Ordner_nach_Namen()
    {
        var folders = new List<(string, string, string?, FolderAttributes)>
        {
            ("INBOX", "INBOX", null, FolderAttributes.None),
            ("Junk-E-Mail", "Junk-E-Mail", null, FolderAttributes.None),
            ("Junk", "Junk", null, FolderAttributes.None),
            ("Sent", "Sent", null, FolderAttributes.None),
            ("INBOX.Trash", "Trash", "INBOX", FolderAttributes.None),
        };
        Assert.Equal(new string?[] { "inbox", null, "junk", "sent", "trash" }, MailSync.AssignSpecials(folders));
        // Im Konto selbst festgelegt: ein eigener Ordner als Archiv, kein Junk-Ordner - die Erkennung tritt zurück
        var map = new Dictionary<string, string> { ["archive"] = folders[1].Item1, ["junk"] = "" };
        Assert.Equal(new string?[] { "inbox", "archive", null, "sent", "trash" }, MailSync.AssignSpecials(folders, map));
    }
}
