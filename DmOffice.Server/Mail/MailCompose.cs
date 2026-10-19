using System.Net;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>Mails bauen - zum Senden aus der Oberflaeche und fuer Regeln (weiterleiten).</summary>
public static class MailCompose
{
    public sealed class Draft
    {
        public string To { get; set; } = "";
        public string Cc { get; set; } = "";
        public string Bcc { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Html { get; set; } = "";
        public string? InReplyTo { get; set; }
        public List<string> References { get; set; } = new();
        /// <summary>Beantwortete Mail - bekommt \Answered.</summary>
        public string? ReplyFolder { get; set; }
        public uint? ReplyUid { get; set; }
        /// <summary>Weitergeleitete Mail - die gewaehlten Anhaenge kommen mit.</summary>
        public string? ForwardFolder { get; set; }
        public uint? ForwardUid { get; set; }
        public List<int> ForwardAttachments { get; set; } = new();
        /// <summary>Die ganze Mail als .eml anhaengen (Als Anlage weiterleiten).</summary>
        public bool ForwardAsAttachment { get; set; }
        /// <summary>Entwurf, den dieser ersetzt.</summary>
        public uint? DraftUid { get; set; }
        public bool HighPriority { get; set; }
        public bool ReadReceipt { get; set; }
        /// <summary>Konto der Original-Mail (ReplyUid, ForwardUid) und des alten Entwurfs (DraftUid),
        /// wenn es nicht das Absenderkonto ist. Leer: das Absenderkonto.</summary>
        public string? SourceAccount { get; set; }
        /// <summary>Geplant senden (ISO-Zeit). Leer: gleich - nach DelaySeconds (Zurueckrufen, hoechstens 30).</summary>
        public string? SendAt { get; set; }
        public int? DelaySeconds { get; set; }
    }

    public static string Wrap(string html) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:Segoe UI,Calibri,Arial,sans-serif;font-size:11pt\">"
        + html + "</body></html>";

    /// <summary>Baut die Mail. Ungueltige Adressen: ArgumentException (die Oberflaeche zeigt die Meldung).</summary>
    /// <param name="linked">Eingebettete Bilder (cid:) - bleiben mit ihrer Content-ID verknuepft.</param>
    public static MimeMessage Build(MailAccount account, Draft d, IEnumerable<(string Name, string Mime, byte[] Data)> attachments,
        IEnumerable<MimePart>? linked = null)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress(string.IsNullOrEmpty(account.DisplayName) ? null : account.DisplayName, account.Email));
        AddAddresses(m.To, d.To, "An");
        AddAddresses(m.Cc, d.Cc, "Cc");
        AddAddresses(m.Bcc, d.Bcc, "Bcc");
        m.Subject = d.Subject;
        if (!string.IsNullOrEmpty(d.InReplyTo)) m.InReplyTo = d.InReplyTo;
        foreach (var r in d.References) m.References.Add(r);
        if (d.HighPriority) m.Priority = MessagePriority.Urgent;
        if (d.ReadReceipt) m.Headers.Add(HeaderId.DispositionNotificationTo, account.Email);
        m.Headers.Add(HeaderId.ContentLanguage, account.Language == "en" ? "en" : "de");

        var builder = new BodyBuilder { HtmlBody = Wrap(d.Html), TextBody = MailHtml.ToText(d.Html) };
        foreach (var (name, mime, data) in attachments)
            builder.Attachments.Add(name, data, ContentType.TryParse(mime, out var t) ? t : new ContentType("application", "octet-stream"));
        foreach (var p in linked ?? Enumerable.Empty<MimePart>())
        {
            // Kopie, nicht das Teil der Original-Mail selbst
            var type = new ContentType(p.ContentType.MediaType, p.ContentType.MediaSubtype);
            if (!string.IsNullOrEmpty(p.ContentType.Name)) type.Name = p.ContentType.Name;
            var part = new MimePart(type)
            {
                Content = new MimeContent(new MemoryStream(Decode(p))),
                ContentTransferEncoding = ContentEncoding.Base64,
                ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
                ContentId = p.ContentId,
            };
            if (!string.IsNullOrEmpty(p.FileName)) part.FileName = p.FileName;
            builder.LinkedResources.Add(part);
        }
        m.Body = builder.ToMessageBody();
        return m;
    }

    private static void AddAddresses(InternetAddressList target, string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!InternetAddressList.TryParse(value, out var list))
            throw new ArgumentException($"Adresse in {field} ungültig: {value.Trim()}");
        target.AddRange(list);
    }

    /// <summary>Teile einer Mail, die als Anhang zaehlen (alles ausser dem Text).
    /// Die Nummer darin ist die Nummer eines Anhangs in der Oberflaeche.</summary>
    public static List<MimePart> Parts(MimeMessage m)
    {
        var html = m.HtmlBody;
        var text = m.TextBody;
        return m.BodyParts.OfType<MimePart>()
            .Where(p => !(p is TextPart tp && !p.IsAttachment && (tp.IsHtml ? tp.Text == html : tp.Text == text)))
            .ToList();
    }

    public static byte[] Decode(MimePart part)
    {
        using var ms = new MemoryStream();
        part.Content?.DecodeTo(ms);
        return ms.ToArray();
    }

    /// <summary>Kopf des Zitats wie in Outlook: Von, Gesendet, An, Betreff - in der Sprache des Kontos.</summary>
    public static string QuoteHeader(MimeMessage o, string language = "de")
    {
        var en = language == "en";
        var date = en
            ? o.Date.ToLocalTime().ToString("dddd, MMMM d, yyyy h:mm tt", new System.Globalization.CultureInfo("en-US"))
            : o.Date.ToLocalTime().ToString("dddd, d. MMMM yyyy HH:mm", new System.Globalization.CultureInfo("de-DE"));
        return "<div style=\"border-top:1px solid #b5c4df;padding-top:6px;margin-top:12px\"><b>" + (en ? "From" : "Von") + ":</b> " + WebUtility.HtmlEncode(o.From.ToString()) +
            "<br><b>" + (en ? "Sent" : "Gesendet") + ":</b> " + date +
            "<br><b>" + (en ? "To" : "An") + ":</b> " + WebUtility.HtmlEncode(o.To.ToString()) +
            (o.Cc.Count > 0 ? "<br><b>Cc:</b> " + WebUtility.HtmlEncode(o.Cc.ToString()) : "") +
            "<br><b>" + (en ? "Subject" : "Betreff") + ":</b> " + WebUtility.HtmlEncode(o.Subject ?? "") + "</div><br>";
    }

    /// <summary>Betreff fuer Antwort/Weiterleitung, ohne Praefix doppelt zu setzen.</summary>
    public static string Prefixed(string? subject, bool forward, string language)
    {
        subject ??= "";
        var known = forward ? new[] { "wg:", "fw:", "fwd:" } : new[] { "aw:", "re:", "antw:" };
        if (known.Any(k => subject.TrimStart().StartsWith(k, StringComparison.OrdinalIgnoreCase))) return subject;
        return (forward ? (language == "en" ? "Fwd: " : "WG: ") : (language == "en" ? "Re: " : "AW: ")) + subject;
    }

    /// <summary>Weiterleitung durch eine Regel: Text der Mail darunter, Anhaenge mit,
    /// eingebettete Bilder (cid:) als verknuepfte Teile.</summary>
    public static MimeMessage Forward(MailAccount account, MimeMessage original, string to)
    {
        var parts = Parts(original);
        var used = new List<MimePart>();
        string body;
        if (original.HtmlBody is { } h)
        {
            // Die Bereinigung kennt cid: nicht und wuerfe die Bilder weg - daher erst auf einen
            // Platzhalter mit erlaubtem Schema, danach wieder auf cid:
            var cids = new List<string>();
            var html = MailHtml.Sanitize(h, true, cid =>
            {
                var part = parts.FirstOrDefault(p => p.ContentId != null && p.ContentId.Trim('<', '>') == cid);
                if (part == null) return null;
                if (!used.Contains(part)) used.Add(part);
                cids.Add(cid);
                return CidPlaceholder + (cids.Count - 1) + "/";
            }).Html;
            // Mit Schraegstrich am Ende passt .../1/ nicht in .../10/
            for (int i = 0; i < cids.Count; i++) html = html.Replace(CidPlaceholder + i + "/", "cid:" + cids[i]);
            body = html;
        }
        else body = MailHtml.FromText(original.TextBody ?? "");
        var d = new Draft
        {
            To = to,
            Subject = Prefixed(original.Subject, true, account.Language),
            Html = QuoteHeader(original, account.Language) + body
        };
        var atts = parts.Where(p => p.IsAttachment && !used.Contains(p))
            .Select(p => (p.FileName ?? "anhang", p.ContentType.MimeType, Decode(p)));
        var m = Build(account, d, atts, used);
        // Kennzeichen gegen Schleifen zwischen zwei Postfaechern mit Weiterleitung
        m.Headers.Add("X-dmOffice-Forwarded", "1");
        return m;
    }

    private const string CidPlaceholder = "https://cid.dmoffice.invalid/";
}
