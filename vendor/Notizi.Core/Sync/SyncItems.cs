using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notizi.Sync
{
    /// <summary>
    /// Was in einer Abgleichdatei steht (docs/konzept-sync.md, Abschnitt 4).
    /// Bewusst ohne Fensterstand, Deckkraft und alles Abgeleitete - das
    /// gehoert dem einzelnen Geraet.
    /// </summary>
    public abstract class SyncItem
    {
        public const string NoteKind = "note";
        public const string FolderKind = "folder";
        public const string GoneKind = "gone";

        [JsonPropertyName("format")] public int Format { get; set; } = 1;

        /// <summary>"note", "folder" oder "gone" - steht in der Datei, damit
        /// der Dateiname nichts verraet.</summary>
        [JsonPropertyName("kind")] public abstract string Kind { get; }

        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("rev")] public long Rev { get; set; }
        [JsonPropertyName("device")] public string Device { get; set; } = "";
        [JsonPropertyName("modified")] public DateTime Modified { get; set; }
        [JsonPropertyName("created")] public DateTime Created { get; set; }

        private static readonly JsonSerializerOptions Options = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>JSON, mit GZip gepackt - Notiztext als HTML schrumpft
        /// dabei auf einen Bruchteil, und verschluesselt laesst er sich
        /// nicht mehr packen.</summary>
        public byte[] ToBytes()
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                JsonSerializer.Serialize(gzip, this, GetType(), Options);
            return buffer.ToArray();
        }

        /// <summary>Liest ein Element der Art, die in ihm steht.</summary>
        public static SyncItem FromBytes(byte[] data)
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
                gzip.CopyTo(buffer);
            var json = buffer.ToArray();

            using var document = JsonDocument.Parse(json);
            var kind = document.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null;
            Type type = kind switch
            {
                NoteKind => typeof(NoteItem),
                FolderKind => typeof(FolderItem),
                GoneKind => typeof(GoneItem),
                _ => throw new InvalidDataException($"Unbekannte Art eines Elements: {kind ?? "(keine)"}.")
            };
            return (SyncItem)(JsonSerializer.Deserialize(json, type, Options)
                ?? throw new InvalidDataException("Leere Abgleichdatei."));
        }
    }

    public sealed class FolderItem : SyncItem
    {
        public override string Kind => FolderKind;

        [JsonPropertyName("parent")] public string? Parent { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("sortOrder")] public double SortOrder { get; set; }
    }

    public sealed class NoteItem : SyncItem
    {
        public override string Kind => NoteKind;

        /// <summary>Version aus einem einfachen Editor (Handy), siehe
        /// <see cref="ProtectedBlocks"/>.</summary>
        public const string BasicEditor = "basic";

        /// <summary>Womit zuletzt bearbeitet: null fuer notizi auf dem PC,
        /// "basic" fuer einen Editor, der nur Absaetze und Listen kann.</summary>
        [JsonPropertyName("editor")] public string? Editor { get; set; }

        [JsonPropertyName("folder")] public string? Folder { get; set; }
        [JsonPropertyName("sortOrder")] public double? SortOrder { get; set; }
        [JsonPropertyName("deleted")] public DateTime? Deleted { get; set; }
        [JsonPropertyName("archived")] public DateTime? Archived { get; set; }

        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("customTitle")] public string? CustomTitle { get; set; }
        [JsonPropertyName("color")] public string Color { get; set; } = "";
        [JsonPropertyName("important")] public bool Important { get; set; }
        [JsonPropertyName("alarm")] public DateTime? Alarm { get; set; }
        [JsonPropertyName("alarmActive")] public bool AlarmActive { get; set; } = true;

        /// <summary>Notiztext als HTML (notizi: HtmlFormat). Bilder darin
        /// als "blob:&lt;name&gt;".</summary>
        [JsonPropertyName("html")] public string Html { get; set; } = "";
        [JsonPropertyName("plain")] public string Plain { get; set; } = "";
        [JsonPropertyName("images")] public List<string> Images { get; set; } = new();

        [JsonPropertyName("attachments")] public List<AttachmentRef> Attachments { get; set; } = new();
        [JsonPropertyName("sources")] public List<SourceRef> Sources { get; set; } = new();

        /// <summary>Alle Blobs, die die Notiz braucht.</summary>
        public IEnumerable<string> Blobs()
        {
            foreach (var image in Images) yield return image;
            foreach (var attachment in Attachments) yield return attachment.Blob;
        }

        /// <summary>
        /// Ob zwei Versionen inhaltlich gleich sind - ohne Revision, Geraet
        /// und Zeitstempel. Zwei Geraete, die unabhaengig dasselbe geaendert
        /// haben (oder dieselbe Notiz zweimal eingerichtet), sind kein
        /// Konflikt.
        /// </summary>
        public bool SameContent(NoteItem other) =>
            Folder == other.Folder && Nullable.Equals(SortOrder, other.SortOrder)
            && Nullable.Equals(Deleted, other.Deleted) && Nullable.Equals(Archived, other.Archived)
            && Title == other.Title && CustomTitle == other.CustomTitle && Color == other.Color
            && Important == other.Important && Nullable.Equals(Alarm, other.Alarm) && AlarmActive == other.AlarmActive
            && Html == other.Html
            && string.Join("|", Attachments.ConvertAll(a => a.Sha256 + "/" + a.Name))
               == string.Join("|", other.Attachments.ConvertAll(a => a.Sha256 + "/" + a.Name))
            && string.Join("|", Sources.ConvertAll(s => s.Url + "/" + s.Title + "/" + s.App))
               == string.Join("|", other.Sources.ConvertAll(s => s.Url + "/" + s.Title + "/" + s.App));
    }

    /// <summary>
    /// Grabstein: Die Notiz oder der Ordner wurde endgueltig geloescht. Ohne
    /// ihn braechte das naechste Geraet, das sie noch hat, sie zurueck.
    /// </summary>
    public sealed class GoneItem : SyncItem
    {
        public override string Kind => GoneKind;

        /// <summary>Was es war: "note" oder "folder".</summary>
        [JsonPropertyName("was")] public string Was { get; set; } = "";
    }

    public sealed class AttachmentRef
    {
        [JsonPropertyName("blob")] public string Blob { get; set; } = "";
        /// <summary>SHA-256 des Inhalts, wie in attachments.sha256 - steht
        /// verschluesselt in der Datei, nicht im Namen.</summary>
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("mime")] public string? Mime { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    public sealed class SourceRef
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("app")] public string? App { get; set; }
    }
}
