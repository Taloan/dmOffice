using System.Text.RegularExpressions;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>Was eine Regel mit einer neuen Mail tun soll - ausgefuehrt im Sync.</summary>
public sealed class RuleOutcome
{
    public bool MarkRead, Flag, Delete;
    public string? MoveTo;
    public List<string> CopyTo { get; } = new();
    public List<string> ForwardTo { get; } = new();
    public List<string> Categories { get; } = new();
    public List<string> Applied { get; } = new();
}

public static class Rules
{
    public static RuleOutcome Evaluate(IEnumerable<MailRule> rules, string accountId, MimeMessage m)
    {
        var outcome = new RuleOutcome();
        foreach (var rule in rules)
        {
            if (!rule.Enabled || (rule.AccountId != null && rule.AccountId != accountId)) continue;
            if (rule.Conditions.Count == 0 || !Matches(rule, m)) continue;
            outcome.Applied.Add(rule.Name);
            bool stop = false;
            foreach (var a in rule.Actions)
            {
                switch (a.Type)
                {
                    case "move": outcome.MoveTo = a.Value; break;
                    case "copy": outcome.CopyTo.Add(a.Value); break;
                    case "markRead": outcome.MarkRead = true; break;
                    case "flag": outcome.Flag = true; break;
                    case "category": if (a.Value.Length > 0) outcome.Categories.Add(a.Value); break;
                    case "delete": outcome.Delete = true; break;
                    case "forward":
                        // Weitergeleitetes nicht noch einmal weiterleiten (zwei Postfaecher, die sich gegenseitig beliefern)
                        if (m.Headers["X-dmOffice-Forwarded"] == null)
                            outcome.ForwardTo.AddRange(a.Value.Split(',', ';').Select(x => x.Trim()).Where(x => x.Contains('@')));
                        break;
                    case "stop": stop = true; break;
                }
            }
            if (stop) break;
        }
        return outcome;
    }

    public static bool Matches(MailRule rule, MimeMessage m)
    {
        var results = rule.Conditions.Select(c => Matches(c, m));
        return rule.Match == "any" ? results.Any(x => x) : results.All(x => x);
    }

    private static bool Matches(RuleCondition c, MimeMessage m)
    {
        IEnumerable<string> values = c.Field switch
        {
            "from" => m.From.Mailboxes.Select(a => $"{a.Name} <{a.Address}>"),
            "to" => m.To.Mailboxes.Concat(m.Cc.Mailboxes).Select(a => $"{a.Name} <{a.Address}>"),
            "subject" => new[] { m.Subject ?? "" },
            "body" => new[] { MailStore.BodyText(m) },
            "any" => new[] { m.From.ToString(), m.To.ToString(), m.Cc.ToString(), m.Subject ?? "", MailStore.BodyText(m) },
            var f when f.StartsWith("header:") => m.Headers.Where(h => h.Field.Equals(f[7..], StringComparison.OrdinalIgnoreCase)).Select(h => h.Value),
            _ => Array.Empty<string>()
        };
        var list = values.ToList();
        if (c.Op == "notContains") return list.All(v => !v.Contains(c.Value, StringComparison.OrdinalIgnoreCase));
        return list.Any(v => c.Op switch
        {
            "equals" => v.Trim().Equals(c.Value.Trim(), StringComparison.OrdinalIgnoreCase)
                        || (c.Field is "from" or "to" && v.Contains("<" + c.Value.Trim() + ">", StringComparison.OrdinalIgnoreCase)),
            "startsWith" => v.StartsWith(c.Value, StringComparison.OrdinalIgnoreCase),
            "endsWith" => v.TrimEnd('>').EndsWith(c.Value, StringComparison.OrdinalIgnoreCase),
            "regex" => SafeRegex(v, c.Value),
            _ => v.Contains(c.Value, StringComparison.OrdinalIgnoreCase)
        });
    }

    private static bool SafeRegex(string input, string pattern)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)); }
        catch { return false; }
    }
}
