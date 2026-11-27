using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;
using Microsoft.AspNetCore.Http.Features;

namespace DmOffice.Emergency;

/// <summary>
/// Seite hinter dem Link eines Benutzers ohne Konto (/n/{Link}): anfordern, Stand sehen, zurückziehen - und nach Ablauf der Frist das
/// Passwort für das angelegte Konto festlegen. Ohne Anmeldung, auf dem Server als HTML gebaut, ohne Skripte. Anfordern und Passwort
/// verlangen den Notfallcode des Pakets - wie bei Benutzern mit Konto: ein weitergegebener Link allein genügt nicht.
/// </summary>
public static class EmergencyPage
{
    private sealed record Hit(EmergencyPackage Package, EmergencyContact Contact, string Token, string Lang);

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string T(string lang, string de, string en) => lang == "en" ? en : de;
    private static string Ip(HttpContext http) => "notfall-ip:" + Auth.IpKey(http.Connection.RemoteIpAddress);
    private static string Lang(HttpContext http) =>
        http.Request.Headers.AcceptLanguage.ToString().TrimStart().StartsWith("de", StringComparison.OrdinalIgnoreCase)
        || http.Request.Headers.AcceptLanguage.Count == 0 ? "de" : "en";

    private static (Hit? Hit, IResult? Error) Resolve(HttpContext http, string token)
    {
        Headers(http);
        var lang = Lang(http);
        var ip = Ip(http);
        if (Auth.Blocked(ip)) return (null, Message(lang, T(lang, "Zu viele Versuche. Bitte später noch einmal.", "Too many attempts. Please try again later."), 429));
        if (http.RequestServices.GetRequiredService<EmergencyStore>().ByToken(token) is not { } found)
        {
            Auth.Failed(ip, "Link eines Notfallzugangs");
            return (null, Message(lang, T(lang, "Diesen Link gibt es nicht (mehr).", "This link does not exist (any more)."), 404));
        }
        return (new Hit(found.Package, found.Contact, token, lang), null);
    }

    /// <summary>Anfragender dieser Person: das angelegte Konto, sonst "c:…".</summary>
    private static string Requester(EmergencyContact c) => c.UserId ?? EmergencyStore.ContactPrefix + c.Id;

    // ---------------- Notfallcode ----------------

    /// <summary>Notfallcode prüfen (dieselbe Prüfung und Fehlversuchsgrenze wie im Programm); Rückgabe: Fehlertext oder null.</summary>
    private static string? CheckCode(HttpContext http, Hit hit, string code)
    {
        var store = http.RequestServices.GetRequiredService<EmergencyStore>();
        var me = EmergencyStore.ContactPrefix + hit.Contact.Id;
        if (EmergencyApi.Unlock(hit.Package, me, code, store) != null) return null;
        Auth.Failed(Ip(http), "Notfallcode eines Notfallzugangs");
        return EmergencyApi.WrongCodeText(hit.Package.Id, me, hit.Lang);
    }

    // ---------------- Seiten ----------------

    private const string Css = "body{font-family:system-ui,-apple-system,Segoe UI,sans-serif;max-width:640px;margin:0 auto;padding:24px 16px;line-height:1.5;color:#222;background:#fafafa}"
        + "h1{font-size:1.4em;margin:.2em 0 .6em}.muted{color:#666;font-size:.9em}label{display:block;margin-top:10px}"
        + "input{font:inherit;width:100%;box-sizing:border-box;padding:8px;border:1px solid #888;border-radius:4px;background:#fff;color:#222}"
        + "button{font:inherit;padding:8px 20px;border:0;border-radius:4px;background:#0f6cbd;color:#fff;cursor:pointer;margin-top:12px}"
        + "button.second{background:transparent;color:inherit;border:1px solid #888}.err{color:#b10e1c}.ok{color:#0e700e}"
        + "@media(prefers-color-scheme:dark){body{color:#eee;background:#1f1f1f}.muted{color:#aaa}.err{color:#ff8a8a}.ok{color:#8fd68f}a{color:#6cb8ff}"
        + "input{background:#2b2b2b;color:#eee;border-color:#777}}";

    private static IResult Page(string lang, string body, int status = 200)
    {
        var html = $"<!doctype html><html lang=\"{lang}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<meta name=\"robots\" content=\"noindex,nofollow,noarchive\"><meta name=\"referrer\" content=\"same-origin\"><title>dmOffice</title>"
            + $"<style>{Css}</style></head><body>{body}</body></html>";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    private static IResult Message(string lang, string text, int status) => Page(lang, $"<h1>dmOffice</h1><p>{E(text)}</p>", status);

    private static void Headers(HttpContext http)
    {
        var h = http.Response.Headers;
        h["Referrer-Policy"] = "same-origin"; // nicht no-referrer: sonst schickt der Browser über http „Origin: null“ und SameOrigin lehnt ab
        h["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        h["Cache-Control"] = "no-store";
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    }

    private static string When(HttpContext http, Hit hit, DateTime utc)
    {
        var l = hit.Lang;
        try
        {
            var zone = http.RequestServices.GetRequiredService<Spaces>().Get(hit.Package.Owner).Zone;
            return TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString(l == "en" ? "MMMM d, yyyy, HH:mm" : "dd.MM.yyyy, HH:mm 'Uhr'", new CultureInfo(l == "en" ? "en-US" : "de-DE"));
        }
        catch (Exception) { return utc.ToString("dd.MM.yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture); }
    }

    private static string Form(Hit hit, string action, string button, string inner = "", bool second = false) =>
        $"<form method=\"post\" action=\"/n/{E(hit.Token)}/{action}\" autocomplete=\"off\">{inner}<button type=\"submit\"{(second ? " class=\"second\"" : "")}>{E(button)}</button></form>";

    private static string CodeField(string lang) =>
        $"<label for=\"code\">{E(T(lang, "Notfallcode", "Emergency code"))}</label><input id=\"code\" name=\"code\" maxlength=\"40\" autocapitalize=\"characters\" spellcheck=\"false\" placeholder=\"XXXX-XXXX-XXXX-XXXX-XXXX\" autofocus required>";

    /// <summary>Die Seite je nach Stand.</summary>
    private static IResult Show(HttpContext http, Hit hit, string? error = null, int status = 200)
    {
        var l = hit.Lang;
        var p = hit.Package;
        var svc = http.RequestServices.GetRequiredService<EmergencyService>();
        var store = http.RequestServices.GetRequiredService<EmergencyStore>();
        var owner = svc.Name(p.Owner);
        var me = Requester(hit.Contact);
        var r = store.RequestsOf(p.Id).FirstOrDefault(x => x.Requester == me);
        var b = new StringBuilder($"<h1>{E(T(l, "Notfallzugang", "Emergency access"))}: {E(p.Name)}</h1>");
        b.Append($"<p>{E(T(l, $"Hallo {hit.Contact.Name}, {owner} hat dich als Berechtigten für dieses Notfallpaket eingetragen. Im Notfall erhältst du Zugriff auf {svc.Content(p)}.",
            $"Hello {hit.Contact.Name}, {owner} has named you as authorized for this emergency package. In an emergency you get access to: {svc.Content(p)}."))}</p>");
        if (error != null) b.Append($"<p class=\"err\" role=\"alert\">{E(error)}</p>");

        if (hit.Contact.UserId != null && r?.State == EmergencyRequest.Granted)
        {
            var login = http.RequestServices.GetRequiredService<UserStore>().ById(hit.Contact.UserId)?.Login ?? "";
            if (hit.Contact.PasswordSet)
            {
                b.Append($"<p>{E(T(l, $"Du hast Zugriff. Du kannst dich mit dem Anmeldenamen „{login}“ und deinem Passwort anmelden.", $"You have access. You can sign in with the user name \"{login}\" and your password."))}</p>");
                b.Append($"<p><a href=\"/\">{E(T(l, "Zur Anmeldung", "Sign in"))}</a></p>");
            }
            else
            {
                b.Append($"<p>{E(T(l, $"Die Frist ist abgelaufen, ohne dass jemand widersprochen hat. dmOffice hat für dich ein Konto mit dem Anmeldenamen „{login}“ angelegt.",
                    $"The waiting period has ended without objection. dmOffice has created an account for you with the user name \"{login}\"."))}</p>");
                b.Append($"<p>{E(T(l, "Mit dem Notfallcode kannst du hier dein Passwort festlegen. Danach kannst du dich mit Anmeldename und Passwort anmelden.",
                    "With the emergency code you can set your password here. After that you can sign in with your user name and password."))}</p>");
                b.Append(Form(hit, "password", T(l, "Passwort festlegen", "Set password"), CodeField(l)
                    + $"<label for=\"pw\">{E(T(l, "Neues Passwort (mindestens 8 Zeichen)", "New password (at least 8 characters)"))}</label><input type=\"password\" id=\"pw\" name=\"password\" minlength=\"8\" maxlength=\"1000\" autocomplete=\"new-password\" required>"
                    + $"<label for=\"pw2\">{E(T(l, "Passwort wiederholen", "Repeat password"))}</label><input type=\"password\" id=\"pw2\" name=\"password2\" minlength=\"8\" maxlength=\"1000\" autocomplete=\"new-password\" required>"));
            }
        }
        else if (hit.Contact.UserId != null && r?.State == EmergencyRequest.Revoked)
            b.Append($"<p>{E(T(l, "Der Zugriff wurde zurückgenommen.", "The access has been revoked."))}</p>");
        else if (r?.State == EmergencyRequest.Open)
        {
            var mail = hit.Contact.Email.Length > 0;
            b.Append($"<p>{E(T(l, $"Deine Anforderung läuft. Bestätigt bis {When(http, hit, r.Due)} niemand, dass kein Notfall vorliegt, erhältst du Zugriff. Den Stand kannst du jederzeit über diesen Link sehen." + (mail ? " Zusätzlich erhältst du eine Mail." : ""),
                $"Your request is running. If nobody confirms by {When(http, hit, r.Due)} that there is no emergency, you get access. You can check the status with this link at any time." + (mail ? " You will also receive an email." : "")))}</p>");
            b.Append(Form(hit, "withdraw", T(l, "Anforderung zurückziehen", "Withdraw request"), second: true));
        }
        else
        {
            if (r?.State == EmergencyRequest.Denied)
                b.Append($"<p>{E(T(l, $"Zuletzt wurde bestätigt, dass kein Notfall vorliegt ({When(http, hit, r.Decided ?? r.Created)}).", $"It was last confirmed that there is no emergency ({When(http, hit, r.Decided ?? r.Created)})."))}</p>");
            if (p.PublicKey == null)
                b.Append($"<p class=\"err\">{E(EmergencyApi.NoCodeText(owner, l))}</p>");
            else
            {
                b.Append($"<p>{E(T(l, $"Liegt ein Notfall vor, kannst du hier mit dem Notfallcode den Zugriff anfordern. Den Notfallcode hat {owner} dir gegeben, zum Beispiel ausgedruckt mit diesem Link.",
                    $"If there is an emergency, you can request access here with the emergency code. {owner} gave you the emergency code, for example printed together with this link."))}</p>");
                b.Append($"<p class=\"muted\">{E(T(l, $"Nach der Anforderung werden alle benachrichtigt, die widersprechen dürfen. Widerspricht innerhalb von {Days(l, p.WaitDays)} niemand, erhältst du Zugriff.",
                    $"After the request, everyone who may object is notified. If nobody objects within {Days(l, p.WaitDays)}, you get access."))}</p>");
                b.Append(Form(hit, "request", T(l, "Zugriff anfordern", "Request access"), CodeField(l)));
            }
        }
        return Page(l, b.ToString(), status);
    }

    private static string Days(string l, int n) => l == "en" ? (n == 1 ? "1 day" : $"{n} days") : (n == 1 ? "einem Tag" : $"{n} Tagen");

    /// <summary>Nur von der eigenen Seite abgeschickte Formulare.</summary>
    private static bool SameOrigin(HttpContext http)
    {
        var site = http.Request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0) return site == "same-origin";
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;
        return origin.Equals($"{http.Request.Scheme}://{http.Request.Host}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals(PublicAddress.Origin(http), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IFormCollection?> FormOf(HttpContext http, CancellationToken ct)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = 16 * 1024;
        try { return await http.Request.ReadFormAsync(ct); }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException or IOException) { return null; }
    }

    private static IResult Back(string token) => Results.Redirect("/n/" + token, permanent: false, preserveMethod: false);

    // ---------------- Routen ----------------

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/n/{token}").RequireRateLimiting("public");

        g.MapGet("", (string token, HttpContext http) =>
        {
            var (hit, error) = Resolve(http, token);
            return hit == null ? error! : Show(http, hit);
        });

        g.MapPost("/request", async (string token, HttpContext http, CancellationToken ct) =>
        {
            var (hit, error) = Resolve(http, token);
            if (hit == null) return error!;
            if (!SameOrigin(http)) return Message(hit.Lang, "Anfrage abgelehnt.", 403);
            if (await FormOf(http, ct) is not { } form) return Show(http, hit, status: 400);
            var store = http.RequestServices.GetRequiredService<EmergencyStore>();
            if (hit.Package.PublicKey == null) return Show(http, hit, status: 400);
            if (CheckCode(http, hit, form["code"].ToString()) is { } wrong) return Show(http, hit, wrong, status: 403);
            try
            {
                var r = store.Ask(hit.Package.Id, Requester(hit.Contact));
                http.RequestServices.GetRequiredService<EmergencyService>().Asked(store.Package(hit.Package.Id)!, r);
            }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException) { return Show(http, hit, e.Message, status: 400); }
            return Back(token);
        }).DisableAntiforgery();

        g.MapPost("/withdraw", (string token, HttpContext http) =>
        {
            var (hit, error) = Resolve(http, token);
            if (hit == null) return error!;
            if (!SameOrigin(http)) return Message(hit.Lang, "Anfrage abgelehnt.", 403);
            var store = http.RequestServices.GetRequiredService<EmergencyStore>();
            var me = Requester(hit.Contact);
            if (store.RequestsOf(hit.Package.Id).FirstOrDefault(x => x.Requester == me && x.State == EmergencyRequest.Open) is { } r)
                store.Withdraw(r.Id, me);
            return Back(token);
        }).DisableAntiforgery();

        g.MapPost("/password", async (string token, HttpContext http, CancellationToken ct) =>
        {
            var (hit, error) = Resolve(http, token);
            if (hit == null) return error!;
            if (!SameOrigin(http)) return Message(hit.Lang, "Anfrage abgelehnt.", 403);
            if (await FormOf(http, ct) is not { } form) return Show(http, hit, status: 400);
            var pw = form["password"].ToString();
            if (pw != form["password2"].ToString())
                return Show(http, hit, T(hit.Lang, "Die beiden Passwörter stimmen nicht überein.", "The two passwords do not match."), status: 400);
            // Passwort zuerst prüfen, damit ein zu kurzes keinen Fehlversuch beim Notfallcode zählt
            if (pw.Length < 8 || UserStore.PasswordProblem(pw) != null)
                return Show(http, hit, pw.Length < 8 ? T(hit.Lang, "Das Passwort muss mindestens 8 Zeichen lang sein.", "The password must be at least 8 characters long.") : UserStore.PasswordProblem(pw), status: 400);
            if (CheckCode(http, hit, form["code"].ToString()) is { } wrong) return Show(http, hit, wrong, status: 403);
            try { http.RequestServices.GetRequiredService<EmergencyStore>().SetPassword(hit.Package.Id, hit.Contact.Id, pw); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException) { return Show(http, hit, e.Message, status: 400); }
            return Back(token);
        }).DisableAntiforgery();
    }
}
