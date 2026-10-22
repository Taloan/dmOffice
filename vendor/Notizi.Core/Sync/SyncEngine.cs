using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Notizi.Sync
{
    /// <summary>Was ein Durchgang getan hat.</summary>
    public sealed class SyncReport
    {
        public int Uploaded { get; set; }
        public int Downloaded { get; set; }
        public int Deleted { get; set; }
        public int Conflicts { get; set; }

        /// <summary>Notizen und Ordner, die es hier schon gab - unter
        /// anderer GUID, etwa aus einer Kopie derselben Datenbank - und die
        /// mit ihrem Gegenstueck zusammengefuehrt wurden.</summary>
        public int Matched { get; set; }

        /// <summary>Beide Seiten hatten geaendert, an verschiedenen Stellen -
        /// zusammengefuehrt, ohne Kopie.</summary>
        public int Merged { get; set; }

        /// <summary>Was mit einem Element hier geschah, fuer die Anzeige am
        /// Editor: "übernommen von BUERO-PC" und dergleichen.</summary>
        public Dictionary<string, string> Events { get; } = new();

        /// <summary>Elemente, die diesmal nicht gingen - sie kommen im
        /// naechsten Durchgang wieder dran.</summary>
        public List<string> Problems { get; } = new();

        /// <summary>IDs der Notizen und Ordner, die sich hier geaendert
        /// haben - offene Fenster laden sie neu.</summary>
        public HashSet<string> ChangedHere { get; } = new();

        public bool Changed => Downloaded + Deleted + Conflicts > 0;

        public override string ToString() =>
            $"{Uploaded} hoch, {Downloaded} herunter, {Deleted} geloescht, {Conflicts} Konflikte" +
            (Matched > 0 ? $", {Matched} zusammengefuehrt" : "") +
            (Merged > 0 ? $", {Merged} verbunden" : "") +
            (Problems.Count > 0 ? $", {Problems.Count} offen" : "");
    }

    /// <summary>
    /// Ein Abgleich-Durchgang zwischen diesem Geraet und dem Abgleichordner
    /// (docs/konzept-sync.md, Abschnitt 6).
    ///
    /// Je Element: Hat es sich hier seit dem letzten Abgleich geaendert
    /// (Revision), dort (Kennung der Datei), oder beides?
    ///
    ///     nur hier     hochladen
    ///     nur dort     herunterladen
    ///     beides       Konflikt: inhaltlich gleich - nichts zu tun; sonst
    ///                  gewinnt die spaetere Version, die andere wird als
    ///                  eigene Notiz daneben gelegt. Nichts geht verloren.
    ///
    /// Entschieden wird ueber Revision und Kennung, nicht ueber Uhrzeiten -
    /// die Uhren der Geraete gehen nicht gleich. Uhrzeiten entscheiden nur,
    /// welche von zwei Versionen die Notiz bleibt.
    ///
    /// Die Aufrufe an <see cref="ISyncLocal"/> geschehen auf dem Strang, auf
    /// dem der Durchgang begann (kein ConfigureAwait(false) hier) - notizi
    /// braucht dafuer seinen STA-Strang.
    /// </summary>
    public sealed class SyncEngine
    {
        private readonly SyncStore _store;
        private readonly ISyncLocal _local;
        private readonly string _device;

        public SyncEngine(SyncStore store, ISyncLocal local, string device)
        {
            _store = store;
            _local = local;
            _device = device;
        }

        public async Task<SyncReport> RunAsync(CancellationToken ct = default)
        {
            var report = new SyncReport();

            // Neu eingerichtet seit dem Oeffnen? Dann nichts mit dem alten
            // Schluessel schreiben.
            await _store.EnsureSameKeyAsync(ct);

            var clientConflicts = new List<(string File, string Id)>();
            var remote = await _store.ListItemsAsync(clientConflicts, ct);
            var remoteBlobs = await _store.ListBlobsAsync(ct);

            var states = _local.States();
            var entries = _local.Entries().ToDictionary(e => e.Id);
            var tombs = _local.Tombstones().ToDictionary(t => t.Id, t => t.Kind);

            // Gibt es dort Neues und hier noch nie Abgeglichenes, kann es
            // dasselbe sein: zwei Kopien einer Datenbank, die jede fuer sich
            // ihre GUIDs bekommen haben. Erst zusammenfuehren, dann wie
            // gewohnt entscheiden.
            if (await MatchAsync(remote, entries, states, tombs, report, ct) > 0)
                entries = _local.Entries().ToDictionary(e => e.Id);

            var downloads = new List<(string Id, string Tag)>();
            var uploads = new List<(string Id, string Kind, string Expected)>();
            var conflicts = new List<(string Id, string Kind, string Tag)>();
            var gone = new List<(string Id, string Kind, string Expected)>();
            var unchangedWithoutBase = new List<SyncState>();

            foreach (var id in remote.Keys.Union(entries.Keys).Union(states.Keys).Union(tombs.Keys).ToList())
            {
                entries.TryGetValue(id, out var here);
                states.TryGetValue(id, out var state);
                remote.TryGetValue(id, out var tag);

                bool remoteChanged = tag != null && (state == null || tag != state.RemoteTag);

                if (here != null)
                {
                    bool localChanged = state == null || here.Rev != state.BaseRev;

                    if (tag == null)
                        uploads.Add((id, here.Kind, ""));             // neu hier, oder dort verschwunden
                    else if (state == null)
                        conflicts.Add((id, here.Kind, tag));          // beide haben es, ohne gemeinsamen Stand
                    else if (localChanged && remoteChanged)
                        conflicts.Add((id, here.Kind, tag));
                    else if (localChanged)
                        uploads.Add((id, here.Kind, tag));
                    else if (remoteChanged)
                        downloads.Add((id, tag));
                    else if (state.Base == null)
                        unchangedWithoutBase.Add(state);              // Ausgangsstand nachtragen
                }
                else if (tombs.TryGetValue(id, out var kind))
                {
                    if (tag == null)
                    {
                        _local.ForgetTombstone(id);
                        _local.RemoveState(id);
                    }
                    else if (state != null && !remoteChanged)
                    {
                        gone.Add((id, kind, tag));
                    }
                    else
                    {
                        // Dort nach dem letzten Abgleich geaendert: Die
                        // Aenderung gewinnt gegen das Loeschen.
                        _local.ForgetTombstone(id);
                        downloads.Add((id, tag));
                    }
                }
                else if (tag != null)
                {
                    if (state == null || remoteChanged || state.Kind != SyncItem.GoneKind)
                        downloads.Add((id, tag));
                }
                else if (state != null)
                {
                    _local.RemoveState(id);
                }
            }

            await DownloadAsync(downloads, uploads, states, report, ct);
            var copies = await ResolveAsync(conflicts, uploads, states, report, ct);
            foreach (var copy in copies) uploads.Add((copy, SyncItem.NoteKind, ""));
            await ClientConflictsAsync(clientConflicts, uploads, report, ct);
            await UploadAsync(uploads, remoteBlobs, report, ct);
            await WriteGoneAsync(gone, report, ct);

            // Seit dem letzten Abgleich auf keiner Seite geaendert: Die
            // hiesige Version IST die abgeglichene. Abgleichstaende von
            // vor dem dreiseitigen Zusammenfuehren bekommen so ihren
            // Ausgangsstand, einmal und ohne etwas zu uebertragen.
            foreach (var state in unchangedWithoutBase)
            {
                SyncItem item = state.Kind == SyncItem.FolderKind
                    ? _local.ExportFolder(state.Id)
                    : _local.ExportNote(state.Id, new BlobCollector(_store.Crypto));
                _local.SaveState(state with { Base = item.ToBytes() });
            }

            return report;
        }

        // ==================================================================
        //  Zusammenfuehren
        // ==================================================================

        /// <summary>
        /// Stellt Notizen und Ordner, die hier noch nie abgeglichen wurden,
        /// unter die GUID ihres Gegenstuecks dort. Danach sehen beide Seiten
        /// dasselbe Element ohne gemeinsamen Stand - der gewoehnliche Weg
        /// macht daraus "gleich, nichts zu tun" oder einen Konflikt, bei dem
        /// beide Versionen bleiben.
        ///
        ///     Ordner   gleicher Name unter demselben Elternordner
        ///     Notiz    gleicher Inhalt - oder, wenn auf einer Seite schon
        ///              weitergeschrieben wurde, gleicher Titel und gleiches
        ///              Erstelldatum
        ///
        /// Nur eindeutige Paare; bei mehreren Kandidaten bleibt es bei zwei
        /// Elementen. Lieber eine Notiz doppelt als zwei verschiedene
        /// verschmolzen.
        /// </summary>
        private async Task<int> MatchAsync(Dictionary<string, string> remote, Dictionary<string, LocalEntry> entries,
            IReadOnlyDictionary<string, SyncState> states, Dictionary<string, string> tombs,
            SyncReport report, CancellationToken ct)
        {
            var newThere = remote.Keys.Where(id => !entries.ContainsKey(id) && !states.ContainsKey(id) && !tombs.ContainsKey(id)).ToList();
            var neverSynced = entries.Values.Where(e => !states.ContainsKey(e.Id) && !remote.ContainsKey(e.Id)).ToList();
            if (newThere.Count == 0 || neverSynced.Count == 0) return 0;

            var theirs = new List<SyncItem>();
            foreach (var id in newThere)
            {
                try { if (await _store.ReadItemAsync(id, ct) is SyncItem item) theirs.Add(item); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* liest der Durchgang gleich noch einmal */ }
            }

            int matched = 0;

            // Ordner zuerst, Eltern vor Kindern: Ein Unterordner passt erst,
            // wenn sein Elternordner zusammengefuehrt ist.
            var theirFolders = theirs.OfType<FolderItem>().ToList();
            var mineFolders = neverSynced.Where(e => e.Kind == SyncItem.FolderKind).Select(e => e.Id).ToHashSet();
            bool progress = true;
            while (progress && theirFolders.Count > 0 && mineFolders.Count > 0)
            {
                progress = false;
                var mine = mineFolders.Select(_local.ExportFolder).ToList();
                foreach (var folder in theirFolders.ToList())
                {
                    var candidates = mine.Where(m => mineFolders.Contains(m.Id) && m.Name == folder.Name && m.Parent == folder.Parent).ToList();
                    if (candidates.Count != 1) continue;
                    _local.Rekey(candidates[0].Id, folder.Id, SyncItem.FolderKind);
                    mineFolders.Remove(candidates[0].Id);
                    theirFolders.Remove(folder);
                    matched++;
                    progress = true;
                    break;   // Elternbezuege der eigenen haben sich geaendert - neu ausfuehren
                }
            }

            // Notizen: jetzt zeigen beide Seiten auf dieselben Ordner-GUIDs.
            var theirNotes = theirs.OfType<NoteItem>().ToList();
            var mineNotes = neverSynced.Where(e => e.Kind == SyncItem.NoteKind)
                                       .Select(e => _local.ExportNote(e.Id, new BlobCollector(_store.Crypto)))
                                       .ToList();

            // Erst nach Inhalt, dann nach Erstelldatum - jeweils nur Paare,
            // die in beide Richtungen eindeutig sind: Zwei gleiche Notizen
            // dort und eine hier sind kein Paar.
            matched += Pair(theirNotes, mineNotes, (t, m) => m.SameContent(t));
            matched += Pair(theirNotes, mineNotes, (t, m) => Math.Abs((m.Created - t.Created).TotalSeconds) < 1);

            report.Matched += matched;
            return matched;
        }

        private int Pair(List<NoteItem> theirs, List<NoteItem> mine, Func<NoteItem, NoteItem, bool> same)
        {
            var pairs = new List<(NoteItem Theirs, NoteItem Mine)>();
            foreach (var t in theirs)
            {
                var candidates = mine.Where(m => same(t, m)).ToList();
                if (candidates.Count != 1) continue;
                if (theirs.Count(other => same(other, candidates[0])) != 1) continue;
                pairs.Add((t, candidates[0]));
            }

            foreach (var (t, m) in pairs)
            {
                _local.Rekey(m.Id, t.Id, SyncItem.NoteKind);
                theirs.Remove(t);
                mine.Remove(m);
            }
            return pairs.Count;
        }


        // ==================================================================
        //  Herunter
        // ==================================================================

        private async Task DownloadAsync(List<(string Id, string Tag)> downloads,
            List<(string Id, string Kind, string Expected)> uploads, IReadOnlyDictionary<string, SyncState> states,
            SyncReport report, CancellationToken ct)
        {
            var folders = new List<(FolderItem Item, string Tag)>();
            var notes = new List<(NoteItem Item, string Tag)>();

            foreach (var (id, tag) in downloads)
            {
                SyncItem? item;
                try
                {
                    item = await _store.ReadItemAsync(id, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    report.Problems.Add($"{id}: nicht lesbar ({ex.Message})");
                    continue;
                }

                switch (item)
                {
                    case FolderItem folder: folders.Add((folder, tag)); break;
                    case NoteItem note: notes.Add((note, tag)); break;
                    case GoneItem g:
                        if (_local.Entries().Any(e => e.Id == id))
                        {
                            _local.DeleteLocal(id, g.Was);
                            report.Deleted++;
                            report.ChangedHere.Add(id);
                        }
                        _local.SaveState(new SyncState(id, SyncItem.GoneKind, -1, tag));
                        break;
                }
            }

            // Eltern vor Kindern: Ein Ordner, dessen Elternordner in diesem
            // Stapel noch kommt, wartet auf ihn.
            var pending = folders.ToList();
            while (pending.Count > 0)
            {
                var ready = pending.Where(f => f.Item.Parent == null || _local.FolderExists(f.Item.Parent)
                                               || pending.All(p => p.Item.Id != f.Item.Parent)).ToList();
                if (ready.Count == 0) ready = pending.ToList();   // Kreis - dann eben oberste Ebene
                foreach (var (item, tag) in ready)
                {
                    _local.ApplyFolder(item);
                    _local.SaveState(new SyncState(item.Id, SyncItem.FolderKind, _local.RevOf(item.Id, SyncItem.FolderKind), tag, item.ToBytes()));
                    report.Downloaded++;
                    report.ChangedHere.Add(item.Id);
                    pending.Remove((item, tag));
                }
            }

            foreach (var (item, tag) in notes)
            {
                if (item.Editor == NoteItem.BasicEditor && _local.Entries().Any(e => e.Id == item.Id))
                {
                    // Aus einem einfachen Editor: Fehlt darin eine Tabelle,
                    // ein Codeblock oder ein Bild der hiesigen Version, wird
                    // sie nicht uebernommen. Die hiesige bleibt und geht
                    // wieder hoch, die andere liegt als Kopie daneben - so
                    // legt jedes weitere Geraet keine eigene Kopie an.
                    // Verglichen wird mit dem Stand, von dem das Handy ausging -
                    // dem zuletzt abgeglichenen. Die hiesige Version neu
                    // ausgefuehrt kann denselben Inhalt anders schreiben
                    // (notizi traegt nach dem Uebernehmen Schriftangaben an
                    // jeden Block), und dann saehe jeder Block veraendert aus.
                    var reference = states.TryGetValue(item.Id, out var st) && st.Base != null
                                    && SyncItem.FromBytes(st.Base) is NoteItem origin
                        ? origin.Html
                        : _local.ExportNote(item.Id, new BlobCollector(_store.Crypto)).Html;
                    if (!ProtectedBlocks.Kept(reference, item.Html))
                    {
                        var copy = ConflictCopy(item, item.Device);
                        if (await ApplyNoteAsync(copy, report, ct))
                        {
                            report.Conflicts++;
                            report.ChangedHere.Add(copy.Id);
                            uploads.Add((copy.Id, SyncItem.NoteKind, ""));
                            uploads.Add((item.Id, SyncItem.NoteKind, tag));
                        }
                        continue;
                    }
                }

                if (await ApplyNoteAsync(item, report, ct))
                {
                    _local.SaveState(new SyncState(item.Id, SyncItem.NoteKind, _local.RevOf(item.Id, SyncItem.NoteKind), tag, item.ToBytes()));
                    report.Downloaded++;
                    report.ChangedHere.Add(item.Id);
                    report.Events[item.Id] = "übernommen von " + item.Device;
                }
            }
        }

        /// <summary>Holt die Blobs der Notiz und uebernimmt sie. False, wenn
        /// einer fehlt - etwa weil der Sync-Client ihn noch nicht hat.</summary>
        private async Task<bool> ApplyNoteAsync(NoteItem item, SyncReport report, CancellationToken ct)
        {
            var blobs = await FetchBlobsAsync(item, report, ct);
            return blobs != null && _local.ApplyNote(item, blobs);
        }

        private async Task<BlobCollector?> FetchBlobsAsync(NoteItem item, SyncReport report, CancellationToken ct)
        {
            var blobs = new BlobCollector(_store.Crypto);
            foreach (var name in item.Blobs().Distinct())
            {
                var data = await _store.GetBlobAsync(name, ct);
                if (data == null)
                {
                    report.Problems.Add($"{item.Id}: Anhang oder Bild fehlt noch im Abgleichordner");
                    return null;
                }
                blobs.Blobs[name] = data;
            }
            return blobs;
        }

        // ==================================================================
        //  Konflikte
        // ==================================================================

        /// <summary>Gibt die IDs neu angelegter Konfliktkopien zurueck - sie
        /// gehen im selben Durchgang hoch.</summary>
        private async Task<List<string>> ResolveAsync(List<(string Id, string Kind, string Tag)> conflicts,
            List<(string Id, string Kind, string Expected)> uploads, IReadOnlyDictionary<string, SyncState> states,
            SyncReport report, CancellationToken ct)
        {
            var copies = new List<string>();

            foreach (var (id, kind, tag) in conflicts)
            {
                SyncItem? theirs;
                try { theirs = await _store.ReadItemAsync(id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    report.Problems.Add($"{id}: nicht lesbar ({ex.Message})");
                    continue;
                }

                switch (theirs)
                {
                    case null:
                        uploads.Add((id, kind, ""));
                        break;

                    case GoneItem:
                        // Hier nach dem Loeschen dort geaendert: Die Notiz
                        // bleibt und kommt zurueck.
                        uploads.Add((id, kind, tag));
                        break;

                    case FolderItem folder:
                    {
                        // Ordner: spaetere Version gewinnt, ohne Kopie - ein
                        // Ordner hat keinen Inhalt, der verloren ginge.
                        var mine = _local.ExportFolder(id);
                        bool Same(FolderItem a, FolderItem b) => a.Name == b.Name && a.Parent == b.Parent && a.SortOrder == b.SortOrder;

                        // Mit Ausgangsstand: Felder einzeln zusammenfuehren -
                        // hier umbenannt, dort verschoben ergibt beides.
                        var target = states.TryGetValue(id, out var fs) && fs.Base != null
                                     && SyncItem.FromBytes(fs.Base) is FolderItem origin
                            ? NoteMerge.MergeFolder(origin, mine, folder)
                            : folder.Modified > mine.Modified ? folder : mine;

                        if (Same(target, mine) && Same(target, folder))
                        {
                            _local.SaveState(new SyncState(id, SyncItem.FolderKind, mine.Rev, tag, folder.ToBytes()));
                            break;
                        }
                        if (!Same(target, mine))
                        {
                            _local.ApplyFolder(target);
                            report.Downloaded++;
                            report.ChangedHere.Add(id);
                        }
                        if (Same(target, folder))
                            _local.SaveState(new SyncState(id, SyncItem.FolderKind, _local.RevOf(id, SyncItem.FolderKind), tag, folder.ToBytes()));
                        else
                            uploads.Add((id, SyncItem.FolderKind, tag));
                        break;
                    }

                    case NoteItem note:
                    {
                        var mineBlobs = new BlobCollector(_store.Crypto);
                        var mine = _local.ExportNote(id, mineBlobs);

                        if (mine.SameContent(note))
                        {
                            _local.SaveState(new SyncState(id, SyncItem.NoteKind, mine.Rev, tag, note.ToBytes()));
                            break;
                        }

                        // Erst alles von dort holen. Fehlt etwas, bleibt der
                        // Konflikt fuer den naechsten Durchgang - ohne schon
                        // eine Kopie angelegt zu haben, sonst entstuende bei
                        // jedem Versuch eine weitere.
                        var theirBlobs = await FetchBlobsAsync(note, report, ct);
                        if (theirBlobs == null) break;

                        // Dreiseitig: Was nur eine Seite geaendert hat, gilt;
                        // verschiedene Absaetze kommen beide hinein, derselbe
                        // Absatz Wort fuer Wort. Ueberschneidet es sich, stehen
                        // beide Versionen in der Notiz - keine zweite Notiz.
                        // Ohne Ausgangsstand (erstes Verbinden, Umzug) gilt
                        // eine leere Notiz als Ausgang: Gleiches bleibt einfach,
                        // Verschiedenes steht beides da.
                        var noteOrigin = states.TryGetValue(id, out var ns) && ns.Base != null
                                         && SyncItem.FromBytes(ns.Base) is NoteItem known
                            ? known : EmptyOrigin(mine, note);
                        if (NoteMerge.Merge(noteOrigin, mine, note, out bool marked) is NoteItem merged)
                        {
                            var both = new BlobCollector(_store.Crypto);
                            foreach (var (name, data) in mineBlobs.Blobs) both.Blobs[name] = data;
                            foreach (var (name, data) in theirBlobs.Blobs) both.Blobs[name] = data;

                            if (!merged.SameContent(mine))
                            {
                                if (!_local.ApplyNote(merged, both)) { report.Problems.Add($"{id}: Zusammenfuehren nicht moeglich"); break; }
                                report.ChangedHere.Add(id);
                            }

                            if (merged.SameContent(note))
                            {
                                // Ergebnis ist deren Version - nichts hochzuladen.
                                _local.SaveState(new SyncState(id, SyncItem.NoteKind, _local.RevOf(id, SyncItem.NoteKind), tag, note.ToBytes()));
                                report.Downloaded++;
                                report.Events[id] = "übernommen von " + note.Device;
                            }
                            else
                            {
                                uploads.Add((id, SyncItem.NoteKind, tag));
                                report.Merged++;
                                report.Events[id] = "zusammengeführt mit Änderungen von " + note.Device;
                            }
                            if (marked)
                            {
                                report.Conflicts++;
                                report.Events[id] = "gleichzeitig geändert – beide Versionen stehen in der Notiz";
                            }
                            break;
                        }

                        report.Conflicts++;
                        if (note.Modified >= mine.Modified)
                        {
                            // Dort spaeter: die Notiz wird deren Version, die
                            // hiesige liegt als Kopie daneben.
                            var copy = ConflictCopy(mine, _device);
                            if (!_local.ApplyNote(copy, mineBlobs)) { report.Problems.Add($"{id}: Konfliktkopie nicht moeglich"); break; }
                            copies.Add(copy.Id);
                            report.ChangedHere.Add(copy.Id);

                            if (_local.ApplyNote(note, theirBlobs))
                            {
                                _local.SaveState(new SyncState(id, SyncItem.NoteKind, _local.RevOf(id, SyncItem.NoteKind), tag, note.ToBytes()));
                                report.ChangedHere.Add(id);
                                report.Events[id] = "Konflikt: Version von " + note.Device + " übernommen, die eigene liegt als Kopie daneben";
                            }
                        }
                        else
                        {
                            // Hier spaeter: deren Version als Kopie, die
                            // hiesige geht hoch.
                            var copy = ConflictCopy(note, note.Device);
                            if (_local.ApplyNote(copy, theirBlobs))
                            {
                                copies.Add(copy.Id);
                                report.ChangedHere.Add(copy.Id);
                                uploads.Add((id, SyncItem.NoteKind, tag));
                                report.Events[id] = "Konflikt: eigene Version behalten, die von " + note.Device + " liegt als Kopie daneben";
                            }
                        }
                        break;
                    }
                }
            }

            return copies;
        }

        /// <summary>
        /// Konfliktkopien, die ein Sync-Client im Ordner-Modus selbst
        /// angelegt hat. Inhaltlich gleich wie die Notiz hier: weg damit.
        /// Sonst wird daraus eine eigene Notiz, und die Datei verschwindet.
        /// </summary>
        private async Task ClientConflictsAsync(List<(string File, string Id)> files,
            List<(string Id, string Kind, string Expected)> uploads, SyncReport report, CancellationToken ct)
        {
            foreach (var (file, id) in files)
            {
                SyncItem? item;
                try { item = await _store.ReadItemFileAsync(file, id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    report.Problems.Add($"{file}: nicht lesbar ({ex.Message})");
                    continue;
                }

                if (item is NoteItem note)
                {
                    bool same = false;
                    if (_local.Entries().Any(e => e.Id == id))
                        same = _local.ExportNote(id, new BlobCollector(_store.Crypto)).SameContent(note);

                    if (!same)
                    {
                        var copy = ConflictCopy(note, note.Device);
                        if (!await ApplyNoteAsync(copy, report, ct)) continue;
                        uploads.Add((copy.Id, SyncItem.NoteKind, ""));
                        report.ChangedHere.Add(copy.Id);
                        report.Conflicts++;
                    }
                }

                await _store.DeleteItemFileAsync(file, ct);
            }
        }

        /// <summary>
        /// Ausgang, wenn es keinen gibt: dieselbe Notiz ohne Text, ihre Felder
        /// von der frueheren Version - so gilt bei Feldern die spaetere, und im
        /// Text kommt zusammen, was gleich ist, und dazu, was verschieden ist.
        /// </summary>
        private static NoteItem EmptyOrigin(NoteItem mine, NoteItem theirs)
        {
            var earlier = theirs.Modified >= mine.Modified ? mine : theirs;
            var origin = (NoteItem)SyncItem.FromBytes(earlier.ToBytes());
            var head = NoteMerge.Split(mine.Html)?.Head ?? "<div data-notizi=\"1\">";
            origin.Html = head.EndsWith("/>") ? head : head + "</div>";
            origin.Images = new();
            origin.Attachments = new();
            origin.Sources = new();
            return origin;
        }

        private static NoteItem ConflictCopy(NoteItem source, string device)
        {
            var copy = (NoteItem)SyncItem.FromBytes(source.ToBytes());
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Rev = 0;
            var when = source.Modified.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            var title = string.IsNullOrWhiteSpace(source.CustomTitle) ? source.Title : source.CustomTitle;
            // Eine Kopie einer Kopie bekommt nicht noch einen Zusatz dazu -
            // "(Konflikt …) (Konflikt …)" sagt nichts mehr.
            title = System.Text.RegularExpressions.Regex.Replace(title ?? "", @"(\s*\(Konflikt, [^)]*\))+$", "");
            copy.CustomTitle = $"{title} (Konflikt, {device}, {when})";
            return copy;
        }

        // ==================================================================
        //  Hoch
        // ==================================================================

        private async Task UploadAsync(List<(string Id, string Kind, string Expected)> uploads,
            HashSet<string> remoteBlobs, SyncReport report, CancellationToken ct)
        {
            // Ordner vor Notizen, Eltern vor Kindern - ein anderes Geraet,
            // das mitten im Hochladen abgleicht, findet so jeden Ordner, auf
            // den eine Notiz zeigt.
            var folders = uploads.Where(u => u.Kind == SyncItem.FolderKind)
                                 .Select(u => (Item: _local.ExportFolder(u.Id), u.Expected)).ToList();
            var ordered = new List<(FolderItem Item, string Expected)>();
            while (folders.Count > 0)
            {
                var ready = folders.Where(f => f.Item.Parent == null || folders.All(o => o.Item.Id != f.Item.Parent)).ToList();
                if (ready.Count == 0) ready = folders.ToList();
                ordered.AddRange(ready);
                folders.RemoveAll(ready.Contains);
            }

            foreach (var (item, expected) in ordered)
            {
                item.Device = _device;
                await WriteAsync(item, expected, SyncItem.FolderKind, report, ct);
            }

            foreach (var (id, _, expected) in uploads.Where(u => u.Kind == SyncItem.NoteKind))
            {
                var blobs = new BlobCollector(_store.Crypto);
                var item = _local.ExportNote(id, blobs);
                item.Device = _device;

                foreach (var (name, data) in blobs.Blobs)
                {
                    if (remoteBlobs.Contains(name)) continue;
                    await _store.PutBlobAsync(name, data, ct);
                    remoteBlobs.Add(name);
                }

                await WriteAsync(item, expected, SyncItem.NoteKind, report, ct);
            }
        }

        private async Task WriteAsync(SyncItem item, string expected, string kind, SyncReport report, CancellationToken ct)
        {
            try
            {
                var tag = await _store.WriteItemAsync(item, expected, ct);
                _local.SaveState(new SyncState(item.Id, kind, item.Rev, tag, item.ToBytes()));
                report.Uploaded++;
            }
            catch (SyncPreconditionException)
            {
                // Ein anderes Geraet war schneller. Der naechste Durchgang
                // sieht dessen Version und behandelt es als Konflikt.
                report.Problems.Add($"{item.Id}: gleichzeitig anderswo geaendert, folgt im naechsten Durchgang");
            }
        }

        private async Task WriteGoneAsync(List<(string Id, string Kind, string Expected)> gone, SyncReport report, CancellationToken ct)
        {
            foreach (var (id, kind, expected) in gone)
            {
                var item = new GoneItem
                {
                    Id = id, Was = kind, Device = _device,
                    Created = DateTime.UtcNow, Modified = DateTime.UtcNow
                };
                try
                {
                    var tag = await _store.WriteItemAsync(item, expected, ct);
                    _local.SaveState(new SyncState(id, SyncItem.GoneKind, -1, tag));
                    _local.ForgetTombstone(id);
                    report.Uploaded++;
                }
                catch (SyncPreconditionException)
                {
                    report.Problems.Add($"{id}: gleichzeitig anderswo geaendert, folgt im naechsten Durchgang");
                }
            }
        }
    }
}
