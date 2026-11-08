using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;
using DmOffice.Pim;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Aufgaben zuweisen (Version 2): nur an Leute der Liste, Nachricht von Pukki, „Ben soll …“ im Gruppenchat.</summary>
[Collection("server")]
public class TaskAssignTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public TaskAssignTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Zuweisen_nur_an_Mitglieder_mit_Nachricht()
    {
        var anna = _fx.Space("anna");
        PimStore.EnsureDefaults(anna.Db);
        var list = PimStore.CreateCollection(anna.Db, PimStore.Calendar, "haushalt", "Haushalt", null, "VTODO");
        var shares = _fx.Factory.Services.GetRequiredService<ShareStore>();
        shares.Set(anna.User.Id, list.Id, [_fx.Id("ben")]);

        // Wer kommt in Frage: Anna und Ben, nicht Cleo
        var people = (await Json(await _fx.As("anna").GetAsync($"/api/pim/lists/{list.Id}/people"))).EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { _fx.Id("anna"), _fx.Id("ben") }, people);
        // Cleo darf nicht zugewiesen werden
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("anna").PostAsJsonAsync("/api/pim/tasks", new { list = list.Id, title = "Müll rausbringen", assignee = _fx.Id("cleo") })).StatusCode);

        var chat = _fx.Factory.Services.GetRequiredService<ChatStore>();
        Pukki.Ensure(chat, _fx.Id("ben"));
        var benRoom = chat.DirectRoom(_fx.Id("ben"), Pukki.Id)!.Value;
        var before = chat.Messages(_fx.Id("ben"), benRoom).Count;

        // Ben zugewiesen: steht in der Aufgabe, Pukki sagt es Ben mit der Aufgabe zum Antippen
        var saved = await Json(await _fx.As("anna").PostAsJsonAsync("/api/pim/tasks", new { list = list.Id, title = "Müll rausbringen", assignee = _fx.Id("ben") }));
        var id = saved.GetProperty("id").GetString()!;
        var task = (await Json(await _fx.As("anna").GetAsync($"/api/pim/tasks?lists={list.Id}"))).EnumerateArray().First(t => t.GetProperty("id").GetString() == id);
        Assert.Equal(_fx.Id("ben"), task.GetProperty("assignee").GetString());
        var msgs = chat.Messages(_fx.Id("ben"), benRoom);
        Assert.Equal(before + 1, msgs.Count);
        Assert.Contains("Anna hat dir die Aufgabe „Müll rausbringen“ zugewiesen", msgs[^1].Text);
        Assert.Equal(ChatRef.TypeTask, msgs[^1].Refs[0].Type);
        Assert.Equal(id[(id.IndexOf('/') + 1)..], msgs[^1].Refs[0].Item);

        // Ben sieht die Aufgabe in der geteilten Liste mit Zuweisung; erneutes Speichern ohne Wechsel meldet nichts Neues
        var shareId = shares.All().First(s => s.Owner == anna.User.Id && s.Collection == list.Id).Id;
        var benTask = (await Json(await _fx.As("ben").GetAsync($"/api/pim/tasks?lists={-shareId}"))).EnumerateArray().First();
        Assert.Equal(_fx.Id("ben"), benTask.GetProperty("assignee").GetString());
        await Json(await _fx.As("ben").PostAsJsonAsync("/api/pim/tasks", new { list = -shareId, uri = benTask.GetProperty("uri").GetString(), title = "Müll rausbringen", assignee = _fx.Id("ben"), completed = true }));
        Assert.Equal(before + 1, chat.Messages(_fx.Id("ben"), benRoom).Count);
        // Zuweisung entfernen
        await Json(await _fx.As("anna").PostAsJsonAsync("/api/pim/tasks", new { list = list.Id, uri = benTask.GetProperty("uri").GetString(), title = "Müll rausbringen", assignee = "" }));
        task = (await Json(await _fx.As("anna").GetAsync($"/api/pim/tasks?lists={list.Id}"))).EnumerateArray().First(t => t.GetProperty("id").GetString() == id);
        Assert.Equal(JsonValueKind.Null, task.GetProperty("assignee").ValueKind);
    }

    [Fact]
    public void Pukki_versteht_Ben_soll()
    {
        Assert.Equal(("Ben", "den Müll rausbringen"), Pukki.SplitAssignee("Ben soll den Müll rausbringen"));
        Assert.Equal(("Ben", "Müll rausbringen"), Pukki.SplitAssignee("für Ben: Müll rausbringen"));
        Assert.Equal(("Ben", "Müll rausbringen"), Pukki.SplitAssignee("Müll rausbringen – für Ben"));
        Assert.Null(Pukki.SplitAssignee("Müll rausbringen").Who);
    }
}
