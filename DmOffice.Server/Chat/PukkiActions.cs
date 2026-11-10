using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Notes;
using DmOffice.Pim;

namespace DmOffice.Chat;

/// <summary>
/// Knöpfe unter Pukkis Antworten: „Rückgängig“ nimmt genau diese Aktion zurück (auch wenn später Nachrichten dazwischen
/// stehen), „Andere Liste …“ legt sie woanders an, mehrere Ziele und Rückfragen beantwortet man per Knopf. Was ein Knopf
/// tun soll, steht in seinen Daten (die bleiben auf dem Server) - so geht es auch nach einem Neustart noch.
/// Dazu der Essensplan: „abendessen Spaghetti“ wird eine Mahlzeit im Kalender der Art „Essensplan“.
/// </summary>
public static partial class Pukki
{
    /// <summary>Daten eines Knopfs: was angelegt wurde (Rückgängig), was angelegt werden soll (Art, Text, Ziel), woher die Auswahl kam.</summary>
    private sealed record ActData(List<Created>? Made = null, string? Kind = null, string? Text = null, string? Owner = null, long Collection = 0, long Origin = 0);

    /// <summary>Ein Ziel zum Anlegen: eigener oder mit mir geteilter (beschreibbarer) Kalender bzw. Liste.</summary>
    internal sealed record Target(UserSpace Owner, Collection Coll, bool Shared);

    private static readonly JsonSerializerOptions ActJson = new(JsonSerializerDefaults.Web);
    private static string Pack(ActData d) => JsonSerializer.Serialize(d, ActJson);
    private static ActData Unpack(ChatAction a) => (a.Data == null ? null : JsonSerializer.Deserialize<ActData>(a.Data, ActJson)) ?? new();

    private static string KindLabel(string kind) => kind switch
    {
        "event" => L("Als Termin", "As event"), "task" => L("Als Aufgabe", "As task"), "note" => L("Als Notiz", "As note"), "shop" => L("Auf die Einkaufsliste", "To shopping list"), "meal" => L("In den Essensplan", "To meal plan"), _ => kind,
    };

    private static string TargetLabel(Target t) => t.Coll.Name + (t.Shared ? $" ({t.Owner.User.DisplayName})" : "");

    /// <summary>Wohin etwas dieser Art kommen kann: eigene Kalender/Listen, dann geteilte, in die ich schreiben darf.</summary>
    private static List<Target> Targets(IServiceProvider services, UserSpace space, string kind)
    {
        PimStore.EnsureDefaults(space.Db);
        bool Fits(UserDb db, Collection c) => kind switch
        {
            "event" => c.Components.Contains("VEVENT") && CalendarKinds.Of(db, c.Id) == CalendarKinds.Normal,
            "meal" => c.Components.Contains("VEVENT") && CalendarKinds.Of(db, c.Id) == CalendarKinds.Meal,
            "task" => c.Components.Contains("VTODO") && Lists.TypeOf(db, c.Id) == Lists.Tasks,
            "shop" => c.Components.Contains("VTODO") && Lists.TypeOf(db, c.Id) == Lists.Shop,
            _ => false,
        };
        var result = PimStore.Collections(space.Db, PimStore.Calendar).Where(c => Fits(space.Db, c) && !Subscriptions.IsSubscribed(space.Db, c.Id))
            .Select(c => new Target(space, c, false)).ToList();
        foreach (var (share, owner, coll) in SharedLists.For(services.GetRequiredService<ShareStore>(), services.GetRequiredService<Spaces>(), space.User.Id))
            if (Fits(owner.Db, coll) && !share.ReadOnly.Contains(space.User.Id)) result.Add(new Target(owner, coll, true));
        return result;
    }

    /// <summary>Antwort nach dem Anlegen: Knopf „Rückgängig“, gibt es mehrere mögliche Ziele, auch „Andere Liste …“.</summary>
    private static Reply Made(string text, List<Created> made, string kind, string source, int targets)
    {
        // Art und Satz merkt sich auch „Rückgängig“ - für Korrekturen wie „mach 16 Uhr draus“
        var actions = new List<ChatAction> { new("undo", L("Rückgängig", "Undo"), Pack(new ActData(made, kind, source))) };
        if (targets > 1)
            actions.Add(new("move", kind switch { "event" => L("Anderer Kalender …", "Other calendar …"), "meal" => L("Anderer Essensplan …", "Other meal plan …"), _ => L("Andere Liste …", "Other list …") },
                Pack(new ActData(made, kind, source))));
        return new Reply(text, Actions: actions);
    }

    /// <summary>Auswahl per Knopf: je Ziel einer. origin: die Antwort, deren Aktion dorthin umzieht.</summary>
    private static Reply Choose(string question, string kind, string text, IEnumerable<Target> targets, List<Created>? made = null, long origin = 0) =>
        new(question, Actions: targets.Select((t, i) => new ChatAction("pick" + i, TargetLabel(t),
            Pack(new ActData(made, kind, text, t.Owner.User.Id, t.Coll.Id, origin)))).ToList());

    /// <summary>„Zurückgenommen: …“ - abgehakte Artikel und Aufgaben sind danach wieder offen.</summary>
    private static string Undone(List<string> done, IEnumerable<Created>? made = null) =>
        done.Count == 0 ? L("Das war schon weg.", "That was already gone.")
        : made != null && made.Any() && made.All(m => m.Kind == "done") ? L("Wieder offen: ", "Open again: ") + JoinAnd(done) + "."
        : L("Zurückgenommen: ", "Undone: ") + string.Join(", ", done) + ".";

    /// <summary>Ein Knopf unter Pukkis Antwort wurde gedrückt: ausführen und die Antwort darauf schicken.</summary>
    /// <summary>Unter dem Überblick von heute je fällige Aufgabe ein Knopf „✓ …“ - abhaken, ohne die Aufgabenliste zu öffnen.</summary>
    public static IReadOnlyList<ChatAction>? DoneActions(IServiceProvider services, UserSpace space, DateOnly day, DateOnly today)
    {
        if (day != today) return null;
        var items = DueTaskItems(services, space, day, true).Take(8).ToList();
        return items.Count == 0 ? null : items.Select((x, i) => new ChatAction("done" + i, "✓ " + (x.Title.Length > 40 ? x.Title[..39] + "…" : x.Title),
            Pack(new ActData(Owner: x.Owner.User.Id, Collection: x.Collection, Text: x.Uri)))).ToList();
    }

    /// <summary>„Ist erledigt“ ohne Namen - mit Namen („Paket abholen erledigt“) übernimmt MarkDone.</summary>
    private static readonly Regex BareDoneRx = new(@"^(?:(?<what>das|es|alles)\s+)?(?:ist\s+|sind\s+|hab ich\s+|habe ich\s+)?(?:schon\s+|jetzt\s+|auch\s+)?(?:erledigt|gemacht|abgehakt|fertig|done)[\s!.✓]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// „Ist erledigt“: von den heute fälligen Aufgaben (auch überfälligen) die gemeinte abhaken - mit Namen die passende,
    /// ohne Namen die einzige; bei mehreren fragt Pukki mit Knöpfen nach.
    /// </summary>
    private static Reply DoneSaid(Ctx c, string what)
    {
        var items = DueTaskItems(c.Services, c.Space, c.Today, true);
        if (Regex.IsMatch(what, @"^(das|es|alles|die|der|ist|sind|hab|habe)?$", RegexOptions.IgnoreCase)) what = "";
        var hits = what.Length == 0 ? items : items.Where(x => x.Title.Contains(what, StringComparison.OrdinalIgnoreCase) || what.Contains(x.Title, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1)
            return Done(c.Services, c.Space, new ActData(Owner: hits[0].Owner.User.Id, Collection: hits[0].Collection, Text: hits[0].Uri));
        if (items.Count == 0) return "Heute ist nichts mehr offen. 👍";
        if (hits.Count == 0) return new Reply($"„{what}“ finde ich nicht bei dem, was heute fällig ist. Welche ist erledigt?", Actions: DoneActions(c.Services, c.Space, c.Today, c.Today));
        return new Reply("Welche ist erledigt?", Actions: hits.Take(8).Select((x, i) => new ChatAction("done" + i, "✓ " + (x.Title.Length > 40 ? x.Title[..39] + "…" : x.Title),
            Pack(new ActData(Owner: x.Owner.User.Id, Collection: x.Collection, Text: x.Uri)))).ToList());
    }

    /// <summary>Knopf „✓ …“: die Aufgabe als erledigt markieren - eigene Liste oder eine, die beschreibbar mit mir geteilt ist.</summary>
    private static Reply Done(IServiceProvider services, UserSpace space, ActData d)
    {
        UserSpace owner;
        if (d.Owner == space.User.Id) owner = space;
        else
        {
            var share = SharedLists.For(services.GetRequiredService<ShareStore>(), services.GetRequiredService<Spaces>(), space.User.Id)
                .FirstOrDefault(x => x.Owner.User.Id == d.Owner && x.Coll.Id == d.Collection);
            if (share.Owner == null) throw new KeyNotFoundException("Die Liste ist nicht mehr mit dir geteilt.");
            if (share.Share.ReadOnly.Contains(space.User.Id)) throw new InvalidOperationException("Die Liste ist nur zum Lesen mit dir geteilt.");
            owner = share.Owner;
        }
        var task = CalendarModel.Tasks(owner, [d.Collection]).FirstOrDefault(t => t.Uri == d.Text) ?? throw new KeyNotFoundException("Die Aufgabe gibt es nicht mehr.");
        if (task.Completed) return $"„{task.Title}“ war schon erledigt.";
        CalendarModel.SaveTask(owner, PimApi.Input(task, true));
        return $"Erledigt: {task.Title} ✓";
    }

    /// <summary>Kennzeichen der KI am Ende einer Antwort, die ein fertiges Ergebnis ist: „[[REZEPT: Hefeteig]]“, „[[NOTIZ: Packliste]]“.</summary>
    private static readonly Regex KeepMark = new(@"\s*\[\[\s*(?<kind>NOTIZ|REZEPT)\s*:\s*(?<title>[^\]\n]{1,80})\]\]\s*", RegexOptions.IgnoreCase);

    /// <summary>Antwort der KI: das Kennzeichen kommt heraus und wird zum Knopf „Als Notiz speichern“ (Rezepte mit Schlagwort „recipe“).</summary>
    public static Reply AiReply(string text)
    {
        var marks = KeepMark.Matches(text);
        var clean = KeepMark.Replace(text, "\n").Trim();
        if (marks.Count == 0 || clean.Length == 0) return clean.Length == 0 ? text : clean;
        var m = marks[^1];
        var recipe = m.Groups["kind"].Value.Equals("REZEPT", StringComparison.OrdinalIgnoreCase);
        return new Reply(clean, Actions: [new ChatAction("savenote", recipe ? L("Als Rezept speichern", "Save as recipe") : L("Als Notiz speichern", "Save as note"),
            Pack(new ActData(Kind: recipe ? Recipes.Tag : null, Text: m.Groups["title"].Value.Trim())))]);
    }

    /// <summary>Antwort der KI als Notiz: Titel in der ersten Zeile, Rezepte bekommen das Schlagwort und erscheinen im Essensplan.</summary>
    private static Reply SaveAnswer(IServiceProvider services, UserSpace space, string title, string body, string? tag)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("Die Antwort ist leer.");
        var text = (title.Length > 0 ? title + "\n\n" : "") + body.Trim();
        var db = NotesDb.Of(space);
        var id = db.CreateNote(null);
        var html = "<div data-notizi=\"1\">" + string.Concat(text.Split('\n').Select(l => "<p>" + System.Net.WebUtility.HtmlEncode(l.TrimEnd('\r')).Replace("&nbsp;", "&#160;") + "</p>")) + "</div>";
        db.SaveNote(id, html, text.Replace("\n", "\r\n"));
        if (tag != null) db.SetTags(id, [tag]);
        services.GetRequiredService<NotesSync>().Kick(space.User.Id, TimeSpan.FromSeconds(3));
        var label = title.Length > 0 ? title : text.Split('\n')[0];
        return Made($"{(tag != null ? "Als Rezept gespeichert" : "Als Notiz gespeichert")}: „{label}“.", [new Created("note", space.User.Id, 0, null, id, "Notiz „" + label + "“")], "note", text, 0);
    }

    /// <summary>„Als Notiz speichern“, „Speicher das als Rezept“, „Merk dir das“ - Pukkis letzte Antwort aufheben.</summary>
    private static readonly Regex SaveSaid = new(@"^(?:bitte\s+)?(?:kannst du\s+)?(?:"
        + @"(?:speicher|speichere|sicher|sichere|leg|lege|pack|packe|schreib|schreibe|notier|notiere|nimm)\s+(?:mir\s+)?(?:das|dies|es|die antwort|das rezept|den text)\s+(?:bitte\s+)?(?:als|in|in eine|in die|unter|zu den)\s+(?:eine\s+)?(?<kind>notiz|notizen|rezept|rezepte)n?(?:\s+(?:ab|an|auf))?"
        + @"|(?:(?:das|dies|es|die antwort|das rezept|den text)\s+)?(?:bitte\s+)?(?:als|in)\s+(?:eine\s+)?(?<kind>notiz|rezept)\s+(?:speichern|sichern|ablegen|anlegen|aufheben|übernehmen)"
        + @"|(?:merk|merke)\s+dir\s+(?:das|dies|die antwort|das rezept)"
        + @")(?:\s+bitte)?[\s!.?]*$", RegexOptions.IgnoreCase);

    /// <summary>Getippt statt geklickt: speichert die letzte Antwort von Pukki (mit dem Titel aus dem Kennzeichen der KI, wenn es eins gibt).</summary>
    private static Reply? SaveLast(Ctx c, string t)
    {
        if (!SaveSaid.IsMatch(t) || c.Chat == null) return null;
        var said = Regex.IsMatch(t, @"rezept", RegexOptions.IgnoreCase);
        var recent = c.Chat.Messages(c.Space.User.Id, c.Room, limit: 10)
            .Where(m => m.Sender == Id && !m.Deleted && !string.IsNullOrWhiteSpace(m.Text)).OrderByDescending(m => m.Id).ToList();
        // „Als Rezept speichern“: die letzte Antwort, die die KI als Rezept gekennzeichnet hat - sonst die letzte Antwort
        var last = (said ? recent.FirstOrDefault(m => m.Actions?.Any(a => a.Key == "savenote" && Unpack(a).Kind == Recipes.Tag) == true) : null) ?? recent.FirstOrDefault();
        if (last == null) return "Was soll ich speichern? Schreib zum Beispiel: notiz Ideen für den Urlaub";
        var action = last.Actions?.FirstOrDefault(a => a.Key == "savenote");
        if (action == null && Regex.IsMatch(last.Text!, @"^Als (Notiz|Rezept) gespeichert")) return "Das ist schon gespeichert.";
        var d = action != null ? Unpack(action) : null;
        if (action != null) c.Chat.SetActions(last.Id, last.Actions!.Where(a => a.Key != "savenote").ToList() is { Count: > 0 } left ? left : null);
        var recipe = said || d?.Kind == Recipes.Tag;
        return SaveAnswer(c.Services, c.Space, d?.Text ?? "", last.Text!, recipe ? Recipes.Tag : null);
    }

    public static void Act(IServiceProvider services, ChatStore chat, UserSpace space, ChatMessage message, string key)
    {
        var action = message.Actions?.FirstOrDefault(a => a.Key == key);
        Reply reply;
        if (action == null) reply = "Das ist schon erledigt.";
        // Alte Knoepfe nicht mehr ausfuehren - was damals galt, stimmt vielleicht nicht mehr
        else if (DateTime.UtcNow - message.Created > TimeSpan.FromDays(7)) { chat.SetActions(message.Id, null); reply = "Dieser Knopf ist zu alt – sag mir einfach noch einmal, was du möchtest."; }
        // Rueckfrage nach 👎: Rueckgaengig, Anderer Kalender, Andere Zeit, Ich erklaere es
        else if (key.StartsWith("fb-"))
        {
            Reply? fb;
            try { fb = Feedback(services, chat, space, message, key, Unpack(action)); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException) { fb = "Das hat nicht geklappt: " + e.Message; }
            if (fb == null) return;
            reply = fb;
        }
        // Mails lesen erlauben (Ja/Nein unter der Bitte um Erlaubnis)
        else if (key is MailAllow or MailDeny) reply = MailPermission(chat, space, message, key, action);
        // Noch einmal erinnern (Knöpfe unter einer Erinnerung)
        else if (key.StartsWith("snooze-"))
        {
            try { reply = Snooze(chat, space, message, key, action); }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { reply = "Das hat nicht geklappt: " + e.Message; }
        }
        else if (key.StartsWith("say"))
        {
            // Antwort-Knopf („Ja“, „1 Tag vorher“): wie getippt - nur die Antwort-Knöpfe verschwinden, „Rückgängig“ bleibt
            chat.SetActions(message.Id, message.Actions!.Where(a => !a.Key.StartsWith("say")).ToList() is { Count: > 0 } keep ? keep : null);
            reply = Answer(services, space, Unpack(action).Text ?? "", chat, message.Room);
        }
        else
        {
            var mem = MemoryOf(space.User.Id);
            lock (mem)
            {
                try { reply = Run(Unpack(action), mem); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException) { reply = "Das hat nicht geklappt: " + e.Message; }
                Note(mem, reply);
                mem.At = DateTime.UtcNow;
            }
        }
        chat.Post(Id, message.Room, reply.Text, refs: reply.Refs, actions: reply.Actions);

        Reply Run(ActData d, Memory mem)
        {
            var now = Now(space);
            if (key.StartsWith("done"))
            {
                // Nur dieser Knopf verschwindet, die übrigen Aufgaben bleiben abhakbar
                chat.SetActions(message.Id, message.Actions!.Where(a => a.Key != key).ToList() is { Count: > 0 } rest ? rest : null);
                return Done(services, space, d);
            }
            switch (key)
            {
                case "undo":
                    chat.SetActions(message.Id, null);
                    if (mem.LastMade != null && d.Made != null && mem.LastMade.Select(x => x.Uri).SequenceEqual(d.Made.Select(x => x.Uri))) mem.LastMade = null;
                    return Undone(UndoItems(services, d.Made), d.Made);
                case "move":
                {
                    var first = d.Made?.FirstOrDefault();
                    var others = Targets(services, space, d.Kind ?? "").Where(t => first == null || t.Owner.User.Id != first.Owner || t.Coll.Id != first.Collection).ToList();
                    return others.Count == 0 ? L("Es gibt kein anderes Ziel.", "There is no other place.") : Choose(L("Wohin stattdessen?", "Where instead?"), d.Kind!, d.Text ?? "", others, d.Made, message.Id);
                }
                case "savenote":
                    chat.SetActions(message.Id, message.Actions!.Where(a => a.Key != key).ToList() is { Count: > 0 } left ? left : null);
                    return SaveAnswer(services, space, d.Text ?? "", message.Text ?? "", d.Kind);
                case "ingredients":
                {
                    chat.SetActions(message.Id, message.Actions!.Where(a => a.Key != key).ToList());
                    var note = NotesDb.Of(space).BySyncId(d.Text ?? "") ?? throw new KeyNotFoundException("Das Rezept gibt es nicht mehr.");
                    var items = Recipes.Ingredients(note).SelectMany(l => ShopItems(space, l, force: true)).ToList();
                    return items.Count == 0 ? "Im Rezept finde ich keine Zutaten." : AddShop(services, space, items, string.Join(", ", Recipes.Ingredients(note)));
                }
            }
            chat.SetActions(message.Id, null);
            ClearAsk(mem);
            if (key.StartsWith("pick"))
            {
                var target = Targets(services, space, d.Kind ?? "").FirstOrDefault(t => t.Owner.User.Id == d.Owner && t.Coll.Id == d.Collection)
                    ?? throw new InvalidOperationException("Das Ziel gibt es nicht mehr.");
                // Umziehen: das Alte zurücknehmen, dort keine Knöpfe mehr
                if (d.Origin != 0) { UndoItems(services, d.Made); chat.SetActions(d.Origin, null); LearnMove(space, d.Kind!, d.Made, target); }
                return Do(services, space, mem, d.Kind!, d.Text ?? "", now, target);
            }
            return d.Kind is null or "none" ? "Gut, dann lasse ich das." : Do(services, space, mem, d.Kind, d.Text ?? "", now);
        }
    }

    /// <summary>„nein“ als Text: die jüngste noch offene Aktion (höchstens 30 Minuten alt) zurücknehmen.</summary>
    private static string UndoLast(IServiceProvider services, ChatStore? chat, UserSpace space, long room, Memory mem)
    {
        ClearAsk(mem);
        var m = chat == null || room == 0 ? null : chat.Messages(space.User.Id, room).AsEnumerable().Reverse()
            .FirstOrDefault(x => x.Sender == Id && x.Actions?.Any(a => a.Key == "undo") == true && DateTime.UtcNow - x.Created < Remember);
        List<Created>? made;
        if (m != null) { chat!.SetActions(m.Id, null); made = Unpack(m.Actions!.First(a => a.Key == "undo")).Made; }
        else if (mem.LastMade != null) made = mem.LastMade;
        else return "Da gibt es gerade nichts zurückzunehmen.";
        mem.LastMade = null;
        mem.JustMade = false;
        return Undone(UndoItems(services, made), made);
    }

    /// <summary>Neue Nachricht an Pukki: offene Auswahl und Rückfragen gelten nicht mehr (Rückgängig bleibt).</summary>
    public static void CloseChoices(ChatStore chat, string user, long room)
    {
        static bool Choice(ChatAction a) => a.Key.StartsWith("pick") || a.Key.StartsWith("do") || a.Key.StartsWith("say");
        foreach (var m in chat.Messages(user, room, limit: 20))
            if (m.Sender == Id && m.Actions?.Any(Choice) == true)
                chat.SetActions(m.Id, m.Actions.Where(a => !Choice(a)).ToList());
    }

    /// <summary>Abgehakte Aufgabe bzw. abgehakten Artikel wieder öffnen (Rückgängig von „Milch gekauft“).</summary>
    private static void Reopen(UserSpace owner, Created c) => SetDone(owner, c.Collection, c.Uri!, false);

    private static void SetDone(UserSpace owner, long list, string uri, bool done)
    {
        var t = CalendarModel.Tasks(owner, [list]).FirstOrDefault(x => x.Uri == uri) ?? throw new KeyNotFoundException("Den Eintrag gibt es nicht mehr.");
        CalendarModel.SaveTask(owner, new TaskInput
        {
            List = list, Uri = uri, Title = t.Title, Description = t.Description, Due = t.Due, DueAllDay = t.DueAllDay, Priority = t.Priority,
            Categories = t.Categories, Completed = done,
        });
    }

    private static readonly Regex DoneRx = new(
        @"^(?:(?:bitte|dann)\s+)?(?:lösch|lösche|loesch|loesche|streich|streiche|entfern|entferne|nimm|hak|hake)\s+(?:die\s+|den\s+|das\s+)?(?<body>.+?)\s+(?:von|aus)\s+(?:der|meiner|unserer)\s+(?:einkaufs)?liste(?:\s+(?:runter|raus|weg|bitte))?[\s!.]*$" +
        @"|^(?:(?:bitte|dann)\s+)?(?:hak|hake)\s+(?:die\s+|den\s+|das\s+)?(?<body>.+?)\s+ab[\s!.]*$" +
        @"|^(?:die\s+|den\s+|das\s+)?(?<body>.+?)\s+(?:abhaken|abgehakt|gekauft|ist gekauft|sind gekauft|hab ich gekauft|habe ich gekauft|haben wir gekauft|erledigt|ist erledigt|sind erledigt|hab ich erledigt|habe ich erledigt|ist fertig|sind fertig|geschafft|hab ich gemacht|habe ich gemacht|ist gemacht|bezahlt|ist bezahlt|done|ist done|von der liste|streichen|kann weg|können weg)[\s!.]*$",
        RegexOptions.IgnoreCase);

    /// <summary>„Milch gekauft“, „Steuer machen ist erledigt“, „streich Brot von der Liste“: offenen Artikel bzw. offene Aufgabe abhaken.</summary>
    private static Reply? MarkDone(Ctx c, string t)
    {
        var m = DoneRx.Match(t);
        if (!m.Success) return null;
        var body = Regex.Replace(m.Groups["body"].Value.Trim(), @"^(?:ich hab|ich habe|hab|habe|wir haben)\s+", "", RegexOptions.IgnoreCase).Trim();
        if (body.Length == 0 || Regex.IsMatch(body, @"^(das|es|alles|ich|wir)$", RegexOptions.IgnoreCase)) return null;
        var names = Regex.Split(body, @"\s*(?:,|\bund\b)\s*", RegexOptions.IgnoreCase).Where(x => x.Length > 0).ToList();
        var made = new List<Created>();
        var done = new List<string>();
        var missing = new List<string>();
        var lists = ShopLists(c).Concat(Targets(c.Services, c.Space, "task")).ToList();
        foreach (var name in names)
        {
            var key = ItemKey(name);
            var hit = lists.SelectMany(l => CalendarModel.Tasks(l.Owner, [l.Coll.Id]).Where(x => !x.Completed).Select(x => (List: l, Task: x)))
                .FirstOrDefault(x => ItemKey(x.Task.Item ?? x.Task.Title) == key || string.Equals(x.Task.Title, name, StringComparison.OrdinalIgnoreCase))
                is var exact && exact.Task != null ? exact
                : lists.SelectMany(l => CalendarModel.Tasks(l.Owner, [l.Coll.Id]).Where(x => !x.Completed).Select(x => (List: l, Task: x)))
                    .FirstOrDefault(x => x.Task.Title.Contains(name, StringComparison.OrdinalIgnoreCase) && name.Length >= 4);
            if (hit.Task == null) { missing.Add(name); continue; }
            SetDone(hit.List.Owner, hit.List.Coll.Id, hit.Task.Uri, true);
            made.Add(new Created("done", hit.List.Owner.User.Id, hit.List.Coll.Id, hit.Task.Uri, 0, hit.Task.Item ?? hit.Task.Title));
            done.Add(hit.Task.Item ?? hit.Task.Title);
        }
        if (made.Count == 0) return $"{JoinAnd(missing.Select(Cap), quote: true)} finde ich weder auf der Einkaufsliste noch bei deinen offenen Aufgaben.";
        var text = $"Abgehakt: {JoinAnd(done)}." + (missing.Count > 0 ? $" {JoinAnd(missing.Select(Cap), quote: true)} finde ich nicht." : "");
        return Made(text, made, "done", t, 0);
    }

    // ---------------- Essensplan ----------------

    private static readonly (string Key, int Hour)[] Meals = [("Frühstück", 8), ("Mittagessen", 12), ("Abendessen", 18)];

    /// <summary>Ausdrückliche Mahlzeiten - die zählen immer.</summary>
    private static readonly Regex MealNoun = new(@"\b(frühstück|fruehstueck|mittagessen|abendessen|abendbrot)\b", RegexOptions.IgnoreCase);
    /// <summary>„mittags“, „zu Abend“ - nur zusammen mit „gibt es“, „essen“, „kochen“ oder einem Lebensmittel bzw. Rezept.</summary>
    private static readonly Regex MealTime = new(@"\b(zum frühstück|zu mittag|mittags?|zu abend|abends?)\b", RegexOptions.IgnoreCase);
    private static readonly Regex MealVerb = new(@"\b(gibt es|gibt's|gibts|es gibt|essen wir|wir essen|esse ich|ich esse|kochen wir|koche ich|wir kochen|ich koche|kochen|essen)\b", RegexOptions.IgnoreCase);
    private static readonly Regex MealFiller = new(@"\b(gibt es|gibt's|gibts|es gibt|essen wir|wir essen|esse ich|ich esse|kochen wir|koche ich|wir kochen|ich koche|kochen|essen|zum|zu|als|am|um|heute|ist|wird|bei uns)\b|[:,.!]", RegexOptions.IgnoreCase);

    private static string MealKey(string word)
    {
        var w = word.ToLowerInvariant();
        return w.Contains("früh") || w.Contains("frueh") ? "Frühstück" : w.Contains("mittag") ? "Mittagessen" : "Abendessen";
    }

    /// <summary>Das Gericht: der Satz ohne Datum, Mahlzeit und Füllwörter.</summary>
    private static string Dish(string title)
    {
        var s = MealNoun.Replace(title, " ");
        s = MealTime.Replace(s, " ");
        s = MealFiller.Replace(s, " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return Cap(s);
    }

    /// <summary>Sieht der Satz nach einer Mahlzeit aus? (Mahlzeit + Gericht, nicht „heute Abend Kino“, nicht „essen gehen“.)</summary>
    private static bool IsMeal(UserSpace space, string text)
    {
        if (Regex.IsMatch(text, @"\bessen gehen\b|\bgehen\b.*\bessen\b|\bessen\b.*\bgehen\b|zum essen eingeladen|\brestaurant\b|\bessen mit\b", RegexOptions.IgnoreCase)) return false;
        var dish = Dish(PukkiParse.Parse(text, Now(space)).Title);
        // „Mittagessen mit Lisa“ ist eine Verabredung
        if (dish.Length == 0 || Regex.IsMatch(dish, @"^mit\s", RegexOptions.IgnoreCase)) return false;
        bool Food() => PukkiWords.Words(dish).Any(x => PukkiWords.In(x, PukkiWords.ShopWords) || PukkiWords.In(x, PukkiWords.DishWords)) || FindRecipe(space, dish) != null;
        if (MealNoun.IsMatch(text)) return true;
        if (MealTime.IsMatch(text)) return MealVerb.IsMatch(text) || Food();
        // „morgen essen wir Lasagne“, „heute gibt es Pizza“
        return MealVerb.IsMatch(text) && PukkiWords.Words(dish).Count <= 4 && Food();
    }

    /// <summary>Ein Rezept aus den Notizen zum Gericht: gleicher Titel, sonst genau eines, das den Namen enthält.</summary>
    private static Recipes.Entry? FindRecipe(UserSpace space, string dish)
    {
        var db = NotesDb.Of(space);
        var found = Recipes.All(db, db.AllFolders(), dish);
        return found.FirstOrDefault(r => string.Equals(r.Title.Trim(), dish, StringComparison.OrdinalIgnoreCase)) ?? (found.Count == 1 ? found[0] : null);
    }

    // ---------------- Was gibt es zu essen? ----------------

    /// <summary>Frage nach dem Essen: „was“ und ein Essenswort, oder „Essensplan“ allein (mit Tag oder Woche).</summary>
    private static readonly Regex MealAsk = new(@"\bwas\b.*\b(essen|esse|kochen|koche|frühstück|fruehstueck|mittagessen|abendessen|abendbrot|zu mittag|zu abend|zum frühstück|essensplan)\b|^\s*essensplan\b", RegexOptions.IgnoreCase);
    private static readonly Regex WeekWord = new(@"\b(woche|wochenende|nächsten tage|naechsten tage)\b", RegexOptions.IgnoreCase);

    private static string MealIcon(string meal) => meal switch { "Frühstück" => "🥐", "Mittagessen" => "🍲", _ => "🍽️" };

    /// <summary>Alle Essenspläne, die ich sehe: eigene und mit mir geteilte (auch nur zum Lesen).</summary>
    private static List<Target> MealPlans(IServiceProvider services, UserSpace space)
    {
        PimStore.EnsureDefaults(space.Db);
        var result = PimStore.Collections(space.Db, PimStore.Calendar)
            .Where(c => c.Components.Contains("VEVENT") && CalendarKinds.Of(space.Db, c.Id) == CalendarKinds.Meal).Select(c => new Target(space, c, false)).ToList();
        foreach (var (_, owner, coll) in SharedLists.For(services.GetRequiredService<ShareStore>(), services.GetRequiredService<Spaces>(), space.User.Id))
            if (coll.Components.Contains("VEVENT") && CalendarKinds.Of(owner.Db, coll.Id) == CalendarKinds.Meal) result.Add(new Target(owner, coll, true));
        return result;
    }

    /// <summary>
    /// „Was gibt es heute zu essen?“, „Was essen wir morgen?“, „Was gibt's zum Abendessen?“, „Essensplan diese Woche“:
    /// Antwort aus den Essensplänen. null: keine Frage nach dem Essen.
    /// </summary>
    internal static string? MealQuestion(IServiceProvider services, UserSpace space, string text, DateTime now, Func<Target, bool>? planFilter = null)
    {
        if (!MealAsk.IsMatch(text)) return null;
        // Eine Mahlzeit mit Gericht ist ein Eintrag, keine Frage („abendessen Spaghetti“)
        if (!text.Contains('?') && !Regex.IsMatch(text, @"^\s*(hey|hallo|sag mal|du,)?\s*(was|essensplan)\b", RegexOptions.IgnoreCase)) return null;
        var plans = MealPlans(services, space).Where(p => planFilter == null || planFilter(p)).ToList();
        if (plans.Count == 0)
            return "Du hast noch keinen Essensplan. Lege unter Kalender einen Kalender an und stelle als Art „Essensplan“ ein – dann kannst du mir zum Beispiel „abendessen Spaghetti“ schreiben.";
        var today = DateOnly.FromDateTime(now);
        // Satzzeichen weg - „morgen?“ ist sonst kein Tag
        var w = PukkiParse.Parse(Regex.Replace(text, @"[?!.,]", " "), now);
        var from = w.Date ?? today;
        var days = WeekWord.IsMatch(text) ? 7 : 1;
        var only = MealNoun.Match(text) is { Success: true } n ? MealKey(n.Value) : MealTime.Match(text) is { Success: true } t ? MealKey(t.Value) : null;

        var meals = new List<(DateOnly Day, string Meal, string Dish, string? Plan, bool Recipe)>();
        foreach (var p in plans)
        {
            var fromUtc = Dav.Ics.ToUtc(from.ToDateTime(TimeOnly.MinValue), space.Zone);
            foreach (var e in CalendarModel.Events(p.Owner, fromUtc, fromUtc.AddDays(days), [p.Coll.Id]))
            {
                var meal = Meals.Select(x => x.Key).FirstOrDefault(k => e.Categories.Contains(k));
                if (meal == null || (only != null && meal != only)) continue;
                var day = DateOnly.FromDateTime(e.AllDay ? DateTime.Parse(e.Start, CultureInfo.InvariantCulture) : Local(e.Start, space.Zone));
                if (day < from || day >= from.AddDays(days)) continue;
                meals.Add((day, meal, e.Title, plans.Count > 1 ? p.Coll.Name : null, !string.IsNullOrEmpty(e.Recipe)));
            }
        }
        var when = days > 1 ? "in den nächsten sieben Tagen" : PukkiParse.DayText(from, today);
        if (meals.Count == 0)
            return (only != null ? $"Zum {only} steht {when} noch nichts im Essensplan." : $"Für {when} steht noch nichts im Essensplan.") +
                   $" Schreib mir zum Beispiel „{(only ?? "abendessen").ToLowerInvariant()} Spaghetti“, dann trage ich es ein.";
        int Order(string meal) => Array.FindIndex(Meals, x => x.Key == meal);
        string Line((DateOnly Day, string Meal, string Dish, string? Plan, bool Recipe) m) =>
            $"{MealIcon(m.Meal)} {m.Meal} – {m.Dish}{(m.Recipe ? " 📖" : "")}{(m.Plan != null ? $" ({m.Plan})" : "")}";
        if (days == 1)
            return Cap(when) + " im Essensplan:\n" + string.Join("\n", meals.OrderBy(m => Order(m.Meal)).Select(Line));
        return "Im Essensplan:\n" + string.Join("\n", meals.GroupBy(m => m.Day).OrderBy(g => g.Key)
            .Select(g => Cap(PukkiParse.DayText(g.Key, today)) + ":\n" + string.Join("\n", g.OrderBy(m => Order(m.Meal)).Select(m => "  " + Line(m)))));
    }

    /// <summary>Mahlzeit in den Essensplan: Datum aus dem Satz (sonst heute), Mahlzeit als Kategorie, Rezept verknüpft, wenn es eines gibt.</summary>
    private static Reply NewMeal(IServiceProvider services, UserSpace space, string text, DateTime now, Target? to)
    {
        var targets = Targets(services, space, "meal");
        if (targets.Count == 0)
            return "Du hast noch keinen Essensplan. Lege unter Kalender einen Kalender an und stelle als Art „Essensplan“ ein – dann trage ich Mahlzeiten dort ein.";
        var w = PukkiParse.Parse(text, now);
        var dish = Dish(w.Title);
        // Ohne Gericht („essensplan morgen“): zeigen, was drinsteht
        if (dish.Length == 0 || WeekWord.IsMatch(text)) return MealQuestion(services, space, "was " + text + " essensplan?", now) ?? "Was gibt es denn? Zum Beispiel: abendessen Spaghetti";
        if (to == null && targets.Count > 1) return Choose("In welchen Essensplan?", "meal", text, targets);
        var target = to ?? targets[0];
        var m = MealNoun.Match(text) is { Success: true } noun ? noun : MealTime.Match(text);
        var meal = m.Success ? MealKey(m.Value) : "Abendessen";
        var today = DateOnly.FromDateTime(now);
        var day = w.Date ?? today;
        // Die Uhrzeit ist nur fuer andere Kalenderprogramme - der Essensplan liest die Kategorie
        var start = day.ToDateTime(new TimeOnly(Meals.First(x => x.Key == meal).Hour, 0));
        var input = new EventInput
        {
            Calendar = target.Coll.Id, Title = dish, AllDay = false, Busy = false, Categories = [meal],
            Start = Iso(Dav.Ics.ToUtc(start, space.Zone)), End = Iso(Dav.Ics.ToUtc(start.AddHours(1), space.Zone)),
        };
        var recipe = FindRecipe(space, dish);
        if (recipe != null) input.Recipe = recipe.SyncId;
        var id = CalendarModel.SaveEvent(target.Owner, input);
        var made = new List<Created> { new("meal", target.Owner.User.Id, target.Coll.Id, id[(id.IndexOf('/') + 1)..], 0, $"{meal} „{dish}“") };
        var reply = Made($"Im Essensplan „{target.Coll.Name}“: {PukkiParse.DayText(day, today)}, {meal} – {dish}{(recipe != null ? $" (📖 Rezept „{recipe.Title}“)" : "")}.",
            made, "meal", text, targets.Count);
        return recipe == null ? reply
            : reply with { Actions = [.. reply.Actions!, new ChatAction("ingredients", "Zutaten auf die Einkaufsliste", Pack(new ActData(Text: recipe.SyncId)))] };
    }
}
