using System.Collections.Concurrent;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>Die Obergrenze eines Hochlade-Links ist erreicht.</summary>
public sealed class LinkUploadLimitException() : Exception(LinkUploadLimit.Message);

/// <summary>
/// Obergrenze je Hochlade-Link (Kennung uploadlimit): wie viel über einen einzelnen Link insgesamt hochgeladen werden darf,
/// eingestellt in der Verwaltung (Speicher → Kontingente → Aufbewahrung → Freigabelinks; <see cref="FileLimits.LinkUploadMb"/>,
/// 0 = keine eigene Grenze). Es zählt, was über den Link schon abgelegt ist (Schlüssel/Wert-Speicher des Besitzers, je Link),
/// dazu, was gerade hereinkommt: Uploads in Stücken mit der bei Beginn angekündigten Größe, Formular-Uploads Byte für Byte.
/// Eine Sperre je Link hält parallele Anfragen auseinander. Das Kontingent des Besitzers gilt zusätzlich.
/// </summary>
public static class LinkUploadLimit
{
    public const string Message = "Über diesen Link lässt sich nichts mehr hochladen – die Obergrenze für diesen Link ist erreicht.";
    private const string Prefix = "link.uploaded.";

    /// <summary>Je Link: was gerade unterwegs ist (noch nicht abgelegt) und das Abgelegte (einmal gelesen, danach nur hier
    /// fortgeschrieben); zugleich die Sperre des Links.</summary>
    private sealed class Budget
    {
        public long InFlight;
        private long? _uploaded;
        public long Uploaded(UserDb db, string linkId) => _uploaded ??= LinkUploadLimit.Uploaded(db, linkId);
        public void Add(UserDb db, string linkId, long n)
        {
            _uploaded = Uploaded(db, linkId) + n;
            db.Set(Prefix + linkId, _uploaded.Value.ToString());
        }
    }
    private static readonly ConcurrentDictionary<string, Budget> Budgets = new();

    /// <summary>Grenze in Bytes; 0 = keine.</summary>
    public static long Limit(ServerConfig config) =>
        Features.On(Features.UploadLimit) && config.Current.FileLimits.LinkUploadMb > 0 ? config.Current.FileLimits.LinkUploadMb * 1024L * 1024 : 0;

    /// <summary>Über diesen Link schon abgelegt (Bytes).</summary>
    public static long Uploaded(UserDb db, string linkId) => long.TryParse(db.Get(Prefix + linkId), out var v) ? v : 0;

    /// <summary>Link gelöscht: Zähler weg.</summary>
    public static void Forget(UserDb db, string linkId) => db.Set(Prefix + linkId, null);

    private static Budget Of(string owner, string linkId) => Budgets.GetOrAdd(owner + ":" + linkId, _ => new Budget());

    /// <summary>
    /// Neuer Upload in Stücken: passt die angekündigte Größe noch, zusammen mit dem Abgelegten, dem gerade Hereinkommenden und
    /// den anderen laufenden Sitzungen des Links? Dann <paramref name="start"/> unter der Sperre des Links (so zählen zwei
    /// gleichzeitige Anfänge nicht doppelt durch), sonst 413.
    /// </summary>
    public static IResult Start(UserDb db, string owner, string linkId, long limit, long size, Func<IResult> start)
    {
        if (limit <= 0) return start();
        var b = Of(owner, linkId);
        lock (b)
        {
            if (b.Uploaded(db, linkId) + b.InFlight + FilesApi.AnnouncedForLink(db, linkId) + Math.Max(0, size) > limit) return Refused();
            return start();
        }
    }

    public static IResult Refused() => Results.Json(new { error = Message, limit = true }, statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// Platz für einen Upload belegen. <paramref name="bytes"/> sofort (fertiger Upload in Stücken), weitere Bytes beim Lesen über
    /// <see cref="Wrap"/>. Wirft <see cref="LinkUploadLimitException"/>, wenn es nicht passt. Nach dem Ablegen <see cref="Reservation.Commit"/>;
    /// ohne (Fehler, Abbruch) gibt Dispose den Platz wieder frei.
    /// </summary>
    public static Reservation Reserve(UserDb db, string owner, string linkId, long limit, long bytes = 0, bool countAnnounced = false)
    {
        var r = new Reservation(db, Of(owner, linkId), linkId, limit, countAnnounced && limit > 0 ? FilesApi.AnnouncedForLink(db, linkId) : 0,
            Features.On(Features.UploadLimit));
        if (bytes > 0) r.Take(bytes);
        return r;
    }

    /// <param name="track">Auch ohne Grenze mitzählen (Kennung an) - eine später gesetzte Grenze kennt dann das schon Hochgeladene.</param>
    public sealed class Reservation(UserDb db, object gate, string linkId, long limit, long announced, bool track) : IDisposable
    {
        private readonly Budget _b = (Budget)gate;
        private long _held;
        private bool _done;

        /// <summary>Weitere Bytes belegen; passt es nicht mehr, Fehler (nichts belegt).</summary>
        public void Take(long n)
        {
            if (!track || n <= 0) return;
            lock (_b)
            {
                if (limit > 0 && _b.Uploaded(db, linkId) + _b.InFlight + announced + n > limit) throw new LinkUploadLimitException();
                _b.InFlight += n;
                _held += n;
            }
        }

        /// <summary>Abgelegt: das Belegte zählt ab jetzt als hochgeladen.</summary>
        public void Commit()
        {
            if (_done) return;
            _done = true;
            if (_held == 0) return;
            lock (_b)
            {
                _b.InFlight -= _held;
                _b.Add(db, linkId, _held);
                _held = 0;
            }
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            lock (_b) { _b.InFlight -= _held; _held = 0; }
        }

        /// <summary>Strom, der jedes gelesene Stück erst hier belegt.</summary>
        public Stream Wrap(Stream inner) => track ? new Counting(inner, this) : inner;
    }

    /// <summary>Formular-Upload: Größe unbekannt, also beim Lesen zählen.</summary>
    private sealed class Counting(Stream inner, Reservation r) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            r.Take(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            r.Take(n);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }
}
