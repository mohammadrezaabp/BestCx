using System.Text;
using Cx.Application.Abstractions;
using Cx.Domain.Entities;

namespace Cx.Application.Ai;

/// <summary>
/// The topics the AI may choose from right now: the operator's tree with inactive
/// branches pruned. Built from database rows at analysis time, so an edit in the panel
/// reaches the very next classification without a deploy or a publish step.
/// </summary>
public sealed class TopicCatalog
{
    public static readonly TopicCatalog Empty = new(0, Array.Empty<Topic>());

    private readonly Dictionary<Guid, Topic> _byId;
    private readonly Dictionary<string, Topic> _byCode;
    private readonly ILookup<Guid?, Topic> _children;

    public TopicCatalog(int revision, IEnumerable<Topic> topics)
    {
        Revision = revision;
        var all = topics.ToDictionary(t => t.Id);

        // A topic is usable only when it and every ancestor are active: switching off a
        // parent retires its whole branch without the operator touching each child.
        bool Effective(Topic topic)
        {
            var seen = new HashSet<Guid>();
            for (Topic? current = topic; current is not null;
                 current = current.ParentId is { } parent ? all.GetValueOrDefault(parent) : null)
            {
                if (!current.IsActive || !seen.Add(current.Id)) return false;
                if (current.ParentId is { } p && !all.ContainsKey(p)) return false;
            }
            return true;
        }

        var active = all.Values.Where(Effective).ToList();
        _byId = active.ToDictionary(t => t.Id);
        _byCode = active.ToDictionary(t => t.Code, StringComparer.OrdinalIgnoreCase);
        _children = active
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Title, StringComparer.Ordinal)
            .ToLookup(t => t.ParentId);
        Topics = Walk(null).ToList();
    }

    public int Revision { get; }

    /// <summary>Recorded on every AI result as its taxonomy_version.</summary>
    public string Version => $"rev-{Revision}";

    /// <summary>Active topics in tree order (depth first).</summary>
    public IReadOnlyList<Topic> Topics { get; }

    public bool IsEmpty => Topics.Count == 0;

    public Topic? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : _byCode.GetValueOrDefault(code.Trim());

    public bool IsKnown(string? code) => Find(code) is not null;

    /// <summary>Root first, the topic itself last.</summary>
    public IReadOnlyList<Topic> PathOf(Topic topic)
    {
        var path = new List<Topic>();
        for (Topic? current = topic; current is not null;
             current = current.ParentId is { } parent ? _byId.GetValueOrDefault(parent) : null)
            path.Insert(0, current);
        return path;
    }

    /// <summary>
    /// How a chosen node is stored on the result: the root is the category, the chosen node
    /// (when deeper) the subcategory. Codes are unique per taxonomy, so the subcategory alone
    /// identifies the node and its full path.
    /// </summary>
    public (string Category, string? Subcategory) Classification(Topic topic)
    {
        var path = PathOf(topic);
        return (path[0].Code, path.Count > 1 ? topic.Code : null);
    }

    public string TitlePath(Topic topic) => string.Join(" › ", PathOf(topic).Select(t => t.Title));

    /// <summary>The tree as the classifier reads it: one line per topic, indented by depth.</summary>
    public string Render()
    {
        var text = new StringBuilder();
        foreach (var topic in Topics)
        {
            var depth = PathOf(topic).Count - 1;
            text.Append(' ', depth * 2).Append("- [").Append(topic.Code).Append("] ").Append(topic.Title);
            if (!string.IsNullOrWhiteSpace(topic.Description))
                text.Append(" — ").Append(OneLine(topic.Description));
            var keywords = topic.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            if (keywords.Count > 0)
                text.Append(" (examples: ").Append(string.Join("; ", keywords.Select(OneLine))).Append(')');
            text.Append('\n');
        }
        return text.ToString();
    }

    public static async Task<TopicCatalog> LoadAsync(ITaxonomyRepository repository, string tenantId,
        string taxonomyName, CancellationToken ct = default)
    {
        var taxonomy = await repository.EnsureAsync(tenantId, taxonomyName, ct);
        var topics = await repository.ListTopicsAsync(taxonomy.Id, ct);
        return new TopicCatalog(taxonomy.Revision, topics);
    }

    private IEnumerable<Topic> Walk(Guid? parentId)
    {
        foreach (var topic in _children[parentId])
        {
            yield return topic;
            foreach (var child in Walk(topic.Id)) yield return child;
        }
    }

    private static string OneLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
