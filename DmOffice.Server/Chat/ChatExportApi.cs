using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Notes;
using Microsoft.AspNetCore.Http.Features;

namespace DmOffice.Chat;

/// <summary>
/// Unterhaltung exportieren (Chat-Alltag): ZIP mit dem Verlauf als Text („Unterhaltung.txt“, Ortszeit) und den Anhängen,
/// die man herunterladen darf - Gesendetes, Geteiltes und Eigenes. Nur Gezeigtes, Abgelaufenes und Zurückgezogenes steht
/// nur mit Namen im Text. Man bekommt, was man selbst sieht: seit dem eigenen Beitritt, ohne Gelöschtes.
/// </summary>
public static partial class ChatApi
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private static void MapExport(RouteGroupBuilder api)
    {
        api.MapGet("/rooms/{id:long}/export", async (long id, HttpContext http, ChatStore chat, Spaces spaces, UserStore users) =>
        {
            var space = http.Space();
            var me = space.User.Id;
            var room = chat.Room(me, id);
            var title = TitleOf(room, me, users);
            var messages = AllMessages(chat, me, id);

            http.Response.ContentType = "application/zip";
            var file = SafeName($"Chat {title} {DateTime.Now:yyyy-MM-dd}.zip");
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"chat.zip\"; filename*=UTF-8''{Uri.EscapeDataString(file)}";
            // ZipArchive schreibt beim Schliessen synchron - direkt in die Antwort, ohne Zwischendatei
            http.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;
            using (var zip = new ZipArchive(http.Response.Body, ZipArchiveMode.Create, leaveOpen: true))
            {
                var text = new StringBuilder();
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone);
                text.AppendLine($"Unterhaltung „{title}“ – exportiert am {now.ToString("dd.MM.yyyy HH:mm", De)} (Zeitzone {space.Zone.Id})");
                text.AppendLine("Mitglieder: " + string.Join(", ", room.Members.Select(m => NameOf(users, m.Id))));
                text.AppendLine();
                var byId = messages.ToDictionary(m => m.Id);
                foreach (var m in messages)
                {
                    var when = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(m.Created, DateTimeKind.Utc), space.Zone).ToString("dd.MM.yyyy HH:mm", De);
                    if (m.Kind == ChatStore.KindSystem) { text.AppendLine($"[{when}] — {SystemText(m, users)}"); continue; }
                    var line = new StringBuilder($"[{when}] {NameOf(users, m.Sender)}: ");
                    if (m.ReplyTo is { } rt && byId.TryGetValue(rt, out var quoted))
                        line.Append($"(Antwort auf {NameOf(users, quoted.Sender)}: „{Short(quoted.Text ?? string.Join(", ", quoted.Refs.Select(r => r.Name)))}“) ");
                    if (m.Deleted) line.Append("(gelöscht)");
                    else if (m.Text != null) line.Append(m.Text.Replace("\n", "\n    "));
                    text.AppendLine(line.ToString().TrimEnd());
                    if (m.Deleted) continue;
                    for (int i = 0; i < m.Refs.Count; i++)
                        text.AppendLine("    📎 " + await Attach(zip, chat, spaces, me, m, i));
                    if (m.Reactions.Count > 0)
                        text.AppendLine("    " + string.Join("  ", m.Reactions.Select(r => $"{r.Emoji} {string.Join(", ", r.Users.Select(u => NameOf(users, u)))}")));
                }
                var entry = zip.CreateEntry("Unterhaltung.txt");
                await using (var s = entry.Open()) await s.WriteAsync(new UTF8Encoding(true).GetBytes(text.ToString().Replace("\n", "\r\n")));
            }
            await http.Response.Body.FlushAsync();
        });
    }

    /// <summary>Alle Nachrichten, die ich sehe - seitenweise von hinten.</summary>
    private static List<ChatMessage> AllMessages(ChatStore chat, string me, long room)
    {
        var all = new List<ChatMessage>();
        long? before = null;
        while (true)
        {
            var page = chat.Messages(me, room, before, 200);
            all.InsertRange(0, page);
            if (page.Count < 200) return all;
            before = page[0].Id;
        }
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s.Replace('\n', ' ');

    /// <summary>Anhang ins ZIP (wenn erlaubt) - zurück kommt die Zeile für den Text.</summary>
    private static async Task<string> Attach(ZipArchive zip, ChatStore chat, Spaces spaces, string me, ChatMessage m, int i)
    {
        var r = m.Refs[i];
        var kind = r.Type switch
        {
            ChatRef.TypeFile => "Datei", ChatRef.TypeNote => "Notiz", ChatRef.TypeFolder => "Ordner", "event" => "Termin", "task" => "Aufgabe", "contact" => "Kontakt", _ => r.Type,
        };
        var label = $"{kind} „{r.Name}“";
        var state = chat.RefState(r);
        // Standort: Koordinaten nur den eigenen, sonst wie Gezeigtes
        if (r.Type == ChatRef.TypeLocation)
            return r.Owner == me && r.Lat is { } lat && r.Lon is { } lon ? FormattableString.Invariant($"Standort {lat:0.00000}, {lon:0.00000}")
                : "Standort" + (state == "expired" ? " (abgelaufen)" : state == "revoked" ? " (zurückgezogen)" : " (nur gezeigt)");
        if (r.Owner != me && state != "ok") return label + (state == "expired" ? " (abgelaufen)" : " (zurückgezogen)");
        if (r.Owner != me && r.EffectiveMode == ChatRef.Show) return label + " (nur gezeigt)";
        try
        {
            var owner = spaces.Get(r.Owner);
            if (r.Type == ChatRef.TypeFile && r.FileId is { } fid && FileStore.ById(owner.Db, fid) is { IsDir: false } f)
            {
                var path = $"Anhänge/{m.Id}-{i + 1} {SafeName(r.Name)}";
                await using var target = zip.CreateEntry(path, CompressionLevel.Fastest).Open();
                await using (var source = FileStore.OpenRead(owner.Db, f.Path)) await source.CopyToAsync(target);
                return $"{label} → {path}";
            }
            if (r.Type == ChatRef.TypeNote && r.Note != null && NotesDb.Of(owner).BySyncId(r.Note) is { } note)
            {
                var path = $"Anhänge/{m.Id}-{i + 1} {SafeName(r.Name)}.txt";
                await using var target = zip.CreateEntry(path).Open();
                await target.WriteAsync(new UTF8Encoding(true).GetBytes(note.Plain.Replace("\r\n", "\n").Replace("\n", "\r\n")));
                return $"{label} → {path}";
            }
        }
        catch (KeyNotFoundException) { }
        return r.Type is ChatRef.TypeFile or ChatRef.TypeNote ? label + " (nicht mehr verfügbar)" : label;
    }

    /// <summary>Systemnachricht als Satz (wie in Web und App).</summary>
    private static string SystemText(ChatMessage m, UserStore users)
    {
        var by = NameOf(users, m.Sender);
        try
        {
            var p = JsonDocument.Parse(m.Text ?? "{}").RootElement;
            string Get(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var who = p.TryGetProperty("users", out var u) && u.ValueKind == JsonValueKind.Array ? string.Join(", ", u.EnumerateArray().Select(x => NameOf(users, x.GetString() ?? ""))) : "";
            return Get("ev") switch
            {
                "created" => $"{by} hat die Gruppe „{Get("title")}“ erstellt",
                "renamed" => $"{by} hat die Gruppe in „{Get("title")}“ umbenannt",
                "added" => $"{by} hat {who} hinzugefügt",
                "removed" => $"{by} hat {who} entfernt",
                "left" => $"{who} hat die Gruppe verlassen",
                "space" => $"{by} hat für die Gruppe einen Raum eingerichtet: „{Get("title")}“",
                _ => m.Text ?? "",
            };
        }
        catch (JsonException) { return m.Text ?? ""; }
    }
}
