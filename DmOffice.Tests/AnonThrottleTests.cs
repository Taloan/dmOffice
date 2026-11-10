using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Ansteigende Bremse fuer Anfragen ohne Anmeldung (je Adresse, IPv6 je /64).</summary>
public class AnonThrottleTests
{
    private sealed class Clock { public DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc); }

    private static (AnonThrottle T, Clock C) Make(double burst = 10, double perSecond = 1)
    {
        var c = new Clock();
        return (new AnonThrottle(new AnonThrottle.Settings { Burst = burst, PerSecond = perSecond, FirstBlock = TimeSpan.FromSeconds(30) }, () => c.Now), c);
    }

    [Fact]
    public void Normale_Nutzung_bleibt_ungebremst()
    {
        var (t, c) = Make(burst: 10, perSecond: 1);
        // Gleichmaessig 1 Anfrage je Sekunde ueber zehn Minuten: nie gesperrt
        for (int i = 0; i < 600; i++)
        {
            Assert.False(t.Charge("1.2.3.4").NewBlock);
            c.Now = c.Now.AddSeconds(1);
        }
        Assert.Null(t.Blocked("1.2.3.4"));
    }

    [Fact]
    public void Flut_wird_gesperrt_und_jede_weitere_Stufe_dauert_doppelt_so_lange()
    {
        var (t, c) = Make(burst: 10, perSecond: 1);
        AnonThrottle.Outcome last = default;
        for (int i = 0; i < 11; i++) last = t.Charge("1.2.3.4");
        Assert.True(last.NewBlock);
        Assert.Equal(1, last.Level);
        Assert.Equal(TimeSpan.FromSeconds(30), last.Block);
        Assert.NotNull(t.Blocked("1.2.3.4"));

        // Waehrend der Sperre zaehlt nichts weiter
        Assert.False(t.Charge("1.2.3.4").NewBlock);

        // Nach der Sperre gleich wieder fluten: Stufe 2, 60 s
        c.Now = c.Now.AddSeconds(31);
        Assert.Null(t.Blocked("1.2.3.4"));
        for (int i = 0; i < 20 && !last.NewBlock || last.Level < 2; i++) last = t.Charge("1.2.3.4");
        Assert.Equal(2, last.Level);
        Assert.Equal(TimeSpan.FromSeconds(60), last.Block);

        // Stufe 3: 120 s
        c.Now = c.Now.AddSeconds(61);
        do last = t.Charge("1.2.3.4"); while (!last.NewBlock);
        Assert.Equal(3, last.Level);
        Assert.Equal(TimeSpan.FromSeconds(120), last.Block);
    }

    [Fact]
    public void Sperre_ist_auf_eine_Stunde_begrenzt_und_klingt_ab()
    {
        var (t, c) = Make(burst: 2, perSecond: 0.001);
        AnonThrottle.Outcome o = default;
        for (int round = 0; round < 15; round++)
        {
            do o = t.Charge("9.9.9.9"); while (!o.NewBlock);
            c.Now = c.Now + o.Block + TimeSpan.FromSeconds(1);
        }
        Assert.Equal(TimeSpan.FromHours(1), o.Block);

        // Lange Ruhe: die Stufe sinkt wieder, der naechste Verstoss sperrt nur noch kurz
        c.Now = c.Now.AddDays(1);
        do o = t.Charge("9.9.9.9"); while (!o.NewBlock);
        Assert.Equal(1, o.Level);
        Assert.Equal(TimeSpan.FromSeconds(30), o.Block);
    }

    [Fact]
    public void Abgewiesene_Anfragen_kosten_mehr()
    {
        var (t, _) = Make(burst: 9, perSecond: 0.001);
        // 3 abgewiesene (je 3) brauchen das Guthaben auf, die vierte sperrt
        Assert.False(t.Charge("5.5.5.5", 3).NewBlock);
        Assert.False(t.Charge("5.5.5.5", 3).NewBlock);
        Assert.False(t.Charge("5.5.5.5", 3).NewBlock);
        Assert.True(t.Charge("5.5.5.5", 3).NewBlock);
    }

    [Fact]
    public void Adressen_sind_getrennt_und_IPv6_zaehlt_je_64er_Netz()
    {
        Assert.Null(AnonThrottle.KeyOf(null));
        Assert.Null(AnonThrottle.KeyOf(IPAddress.Loopback));
        Assert.Null(AnonThrottle.KeyOf(IPAddress.IPv6Loopback));
        Assert.Equal("1.2.3.4", AnonThrottle.KeyOf(IPAddress.Parse("::ffff:1.2.3.4")));
        // Zwei Adressen aus demselben /64 teilen sich das Guthaben - Rotieren innerhalb des Netzes hilft nicht
        Assert.Equal(AnonThrottle.KeyOf(IPAddress.Parse("2001:db8:1:2::1")), AnonThrottle.KeyOf(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(AnonThrottle.KeyOf(IPAddress.Parse("2001:db8:1:2::1")), AnonThrottle.KeyOf(IPAddress.Parse("2001:db8:1:3::1")));

        var (t, _) = Make(burst: 3, perSecond: 0.001);
        for (int i = 0; i < 4; i++) t.Charge("1.1.1.1");
        Assert.NotNull(t.Blocked("1.1.1.1"));
        Assert.Null(t.Blocked("2.2.2.2"));
    }

    [Fact]
    public async Task In_der_Pipeline_Flut_gesperrt_Angemeldete_kommen_durch()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddAuthorization();
        var app = builder.Build();
        // Absenderadresse fuer den Test aus einer Kopfzeile, angemeldet per Kopfzeile "X-Test-User"
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Test-IP", out var ip)) ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip!);
            if (ctx.Request.Headers.ContainsKey("X-Test-User"))
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "pat")], "test"));
            await next();
        });
        AnonThrottle.Use(app, new AnonThrottle(new AnonThrottle.Settings { Burst = 5, PerSecond = 0.001 }));
        app.MapGet("/api/ping", () => "ok");
        await app.StartAsync();
        try
        {
            var client = app.GetTestClient();
            async Task<HttpStatusCode> Get(string ip, bool user = false)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
                req.Headers.Add("X-Test-IP", ip);
                if (user) req.Headers.Add("X-Test-User", "1");
                return (await client.SendAsync(req)).StatusCode;
            }

            for (int i = 0; i < 6; i++) Assert.Equal(HttpStatusCode.OK, await Get("203.0.113.7"));
            // Guthaben verbraucht, die sechste Anfrage hat gesperrt
            var blocked = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/ping") { Headers = { { "X-Test-IP", "203.0.113.7" } } });
            Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
            Assert.True(blocked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
            // Angemeldet von derselben Adresse: durch
            Assert.Equal(HttpStatusCode.OK, await Get("203.0.113.7", user: true));
            // Andere Adresse: unberuehrt
            Assert.Equal(HttpStatusCode.OK, await Get("198.51.100.1"));
            // Angemeldete Anfragen verbrauchen kein Guthaben
            for (int i = 0; i < 20; i++) Assert.Equal(HttpStatusCode.OK, await Get("198.51.100.2", user: true));
            Assert.Equal(HttpStatusCode.OK, await Get("198.51.100.2"));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public void Speicher_bleibt_begrenzt()
    {
        var c = new Clock();
        var t = new AnonThrottle(new AnonThrottle.Settings { MaxEntries = 1000 }, () => c.Now);
        for (int i = 0; i < 20_000; i++)
        {
            t.Charge("10." + (i / 65536 % 256) + "." + (i / 256 % 256) + "." + (i % 256));
            if (i % 100 == 0) c.Now = c.Now.AddSeconds(11);
        }
        Assert.True(t.Count <= 1000, $"{t.Count} Eintraege");
    }
}
