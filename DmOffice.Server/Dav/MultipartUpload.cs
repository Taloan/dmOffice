using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.WebUtilities;
using DmOffice.Core;
using Microsoft.Net.Http.Headers;

namespace DmOffice.Dav;

/// <summary>Eine Datei aus einem multipart/form-data-Upload; Body gilt nur bis zum naechsten Teil.</summary>
public sealed record UploadPart(string FileName, string? ContentType, Stream Body);

/// <summary>
/// Hochladen als Formular, Teil fuer Teil gelesen statt mit ReadFormAsync: das puffert jede Datei ueber 64 KB
/// unverschluesselt in einer temporaeren Datei und haelt alles bis zum Ende. Hier geht jeder Teil direkt weiter
/// (FileStore.PutAsync, verschluesselt in den BlobStore). Felder ohne Dateinamen werden uebersprungen.
/// Mit <paramref name="sessions"/>: statt des Formulars duerfen fertige Uploads in Stuecken stehen (?upload=ID, auch mehrfach,
/// siehe <see cref="FilesApi.SessionParts"/>) - so muss keine grosse Datei als ein Rumpf kommen.
/// </summary>
public static class MultipartUpload
{
    public static async IAsyncEnumerable<UploadPart> Files(HttpRequest request, [EnumeratorCancellation] CancellationToken ct, UserDb? sessions = null)
    {
        if (sessions != null && request.Query["upload"] is { Count: > 0 } ids)
        {
            await foreach (var part in FilesApi.SessionParts(sessions, ids, ct)) yield return part;
            yield break;
        }
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Erwartet multipart/form-data.");
        var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 200) throw new ArgumentException("Formular ohne Grenze.");
        var reader = new MultipartReader(boundary, request.Body);
        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || !disposition.IsFileDisposition()) continue;
            var name = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
            if (string.IsNullOrEmpty(name)) continue;
            yield return new UploadPart(name, section.ContentType, section.Body);
        }
    }
}
