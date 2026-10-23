using System.Text.RegularExpressions;
using Cx.Application.Options;
using Microsoft.Extensions.Options;

namespace Cx.Application.Privacy;

public sealed record RedactionHit(string RuleName, int Start, int Length, string Original);

public sealed record RedactionOutcome(string RedactedText, IReadOnlyList<RedactionHit> Hits)
{
    public bool ContainsPii => Hits.Count > 0;
}

public interface IRedactionService
{
    /// <summary>
    /// Produces the text that may be sent to a general LLM, plus a restricted-access
    /// redaction map (spec 19). Raw text never leaves the trust boundary.
    /// </summary>
    RedactionOutcome Redact(string? text);
}

public sealed class RedactionService : IRedactionService
{
    private readonly RedactionOptions _options;
    private readonly List<(string Name, Regex Regex)> _rules;

    public RedactionService(IOptions<RedactionOptions> options)
    {
        _options = options.Value;
        _rules = _options.Rules
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern))
            .Select(r => (r.Name, new Regex(r.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2))))
            .ToList();
    }

    public RedactionOutcome Redact(string? text)
    {
        if (string.IsNullOrEmpty(text) || !_options.Enabled)
            return new RedactionOutcome(text ?? string.Empty, Array.Empty<RedactionHit>());

        var hits = new List<RedactionHit>();
        foreach (var (name, regex) in _rules)
        {
            foreach (Match match in regex.Matches(text))
            {
                if (match.Length == 0) continue;
                hits.Add(new RedactionHit(name, match.Index, match.Length, match.Value));
            }
        }

        if (hits.Count == 0)
            return new RedactionOutcome(text, hits);

        // Replace right to left so earlier offsets stay valid, and drop overlaps
        // so two rules matching the same span cannot corrupt the output.
        var ordered = hits.OrderByDescending(h => h.Start).ToList();
        var accepted = new List<RedactionHit>();
        var coveredFrom = int.MaxValue;
        var builder = new System.Text.StringBuilder(text);

        foreach (var hit in ordered)
        {
            if (hit.Start + hit.Length > coveredFrom) continue;
            builder.Remove(hit.Start, hit.Length);
            builder.Insert(hit.Start, string.Format(_options.Mask, hit.RuleName));
            coveredFrom = hit.Start;
            accepted.Add(hit);
        }

        accepted.Reverse();
        return new RedactionOutcome(builder.ToString(), accepted);
    }
}
