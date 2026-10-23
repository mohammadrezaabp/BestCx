using Cx.Application.Abstractions;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Contracts.Security;
using Cx.Domain.Entities;
using Cx.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Cx.Admin.Api;

/// <summary>
/// The topic tree operators maintain from the panel. Every change applies to the next
/// analysis immediately (the AI reads these rows), bumps the taxonomy revision stored on
/// results, and is audited. Operators work with titles and descriptions only: codes are
/// generated here and never edited, because stored analyses reference them.
/// </summary>
public static class TopicEndpoints
{
    /// <summary>Deeper trees stop helping the decision model and make the panel hard to scan.</summary>
    public const int MaxDepth = 4;

    public static IEndpointRouteBuilder MapTopicEndpoints(this IEndpointRouteBuilder app)
    {
        var topics = app.MapGroup("/v1/admin/topics")
            .WithTags("Topics")
            .RequireAuthorization(CxPermissions.CustomerRead);

        topics.MapGet("/", ListAsync).WithName("ListTopics");
        topics.MapPost("/", CreateAsync).WithName("CreateTopic");
        topics.MapPut("/{topicId:guid}", UpdateAsync).WithName("UpdateTopic");
        topics.MapDelete("/{topicId:guid}", DeleteAsync).WithName("DeleteTopic");
        return app;
    }

    private static async Task<IResult> ListAsync(HttpContext ctx, ITaxonomyRepository repository,
        IOptions<AiOptions> ai, CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        var taxonomy = await repository.EnsureAsync(user.TenantId, ai.Value.TaxonomyName, ct);
        var topics = await repository.ListTopicsAsync(taxonomy.Id, ct);
        var usage = await repository.CountUsageAsync(user.TenantId, ct);

        return Results.Ok(new TopicTreeView(
            taxonomy.Revision,
            string.IsNullOrWhiteSpace(ai.Value.DecisionModel) ? ai.Value.Model : ai.Value.DecisionModel,
            user.Has(CxPermissions.TaxonomyManage),
            topics.Select(t => TopicView.From(t, usage.GetValueOrDefault(t.Code))).ToList()));
    }

    private static async Task<IResult> CreateAsync(HttpContext ctx, [FromBody] TopicRequest body,
        ITaxonomyRepository repository, IAuditRepository audit, IOptions<AiOptions> ai, CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!user.Has(CxPermissions.TaxonomyManage)) return Results.Forbid();

        var taxonomy = await repository.EnsureAsync(user.TenantId, ai.Value.TaxonomyName, ct);
        var existing = await repository.ListTopicsAsync(taxonomy.Id, ct);

        var topic = new Topic
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            TaxonomyId = taxonomy.Id,
            Code = NewCode(existing),
            CreatedBy = user.Subject,
            UpdatedBy = user.Subject,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var error = Apply(body, topic, existing, isNew: true);
        if (error is not null) return error;

        await repository.CreateTopicAsync(topic, ct);
        await WriteAuditAsync(audit, user, AuditActions.TopicCreate, topic, before: null, after: topic, ct);

        return Results.Created($"/v1/admin/topics/{topic.Id}", TopicView.From(topic, 0));
    }

    private static async Task<IResult> UpdateAsync(HttpContext ctx, Guid topicId, [FromBody] TopicRequest body,
        ITaxonomyRepository repository, IAuditRepository audit, IOptions<AiOptions> ai, CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!user.Has(CxPermissions.TaxonomyManage)) return Results.Forbid();

        var taxonomy = await repository.EnsureAsync(user.TenantId, ai.Value.TaxonomyName, ct);
        var existing = await repository.ListTopicsAsync(taxonomy.Id, ct);
        var current = existing.FirstOrDefault(t => t.Id == topicId);
        if (current is null) return Results.NotFound(new { error = "Subject not found." });

        var before = Snapshot(current);
        var error = Apply(body, current, existing, isNew: false);
        if (error is not null) return error;

        current.UpdatedBy = user.Subject;
        current.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpdateTopicAsync(current, ct);
        await WriteAuditAsync(audit, user, AuditActions.TopicUpdate, current, before, current, ct);

        var usage = await repository.CountUsageAsync(user.TenantId, ct);
        return Results.Ok(TopicView.From(current, usage.GetValueOrDefault(current.Code)));
    }

    /// <summary>
    /// A topic that analyses already point at is switched off instead of deleted, so history
    /// keeps its label and reports keep adding up.
    /// </summary>
    private static async Task<IResult> DeleteAsync(HttpContext ctx, Guid topicId, ITaxonomyRepository repository,
        IAuditRepository audit, IOptions<AiOptions> ai, CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!user.Has(CxPermissions.TaxonomyManage)) return Results.Forbid();

        var taxonomy = await repository.EnsureAsync(user.TenantId, ai.Value.TaxonomyName, ct);
        var existing = await repository.ListTopicsAsync(taxonomy.Id, ct);
        var topic = existing.FirstOrDefault(t => t.Id == topicId);
        if (topic is null) return Results.NotFound(new { error = "Subject not found." });

        if (existing.Any(t => t.ParentId == topicId))
        {
            return Results.Conflict(new
            {
                error = "This topic has subtopics. First, delete subtopics or move them to another topic."
            });
        }

        var usage = await repository.CountUsageAsync(user.TenantId, ct);
        var used = usage.GetValueOrDefault(topic.Code);
        if (used > 0)
        {
            var before = Snapshot(topic);
            topic.IsActive = false;
            topic.UpdatedBy = user.Subject;
            await repository.UpdateTopicAsync(topic, ct);
            await WriteAuditAsync(audit, user, AuditActions.TopicUpdate, topic, before, topic, ct);
            return Results.Ok(new { deleted = false, deactivated = true, usage_count = used });
        }

        await repository.DeleteTopicAsync(taxonomy.Id, topicId, ct);
        await WriteAuditAsync(audit, user, AuditActions.TopicDelete, topic, Snapshot(topic), after: null, ct);
        return Results.Ok(new { deleted = true, deactivated = false, usage_count = 0 });
    }

    /// <summary>Validates the request against the current tree and copies it onto <paramref name="topic"/>.</summary>
    private static IResult? Apply(TopicRequest body, Topic topic, IReadOnlyList<Topic> existing, bool isNew)
    {
        var title = body.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return Invalid("Enter the subject title.");
        if (title.Length > 200) return Invalid("The subject title is a maximum of 200 characters.");

        var description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        if (description is { Length: > 2000 }) return Invalid("The description of the subject is a maximum of 2000 characters.");

        var keywords = (body.Keywords ?? new List<string>())
            .Select(k => k?.Trim())
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => k!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keywords.Length > 30) return Invalid("A maximum of 30 phrase examples is allowed for each topic.");
        if (keywords.Any(k => k.Length > 200)) return Invalid("Each phrase is a maximum of 200 characters.");

        var byId = existing.ToDictionary(t => t.Id);
        if (body.ParentId is { } parentId)
        {
            if (!byId.ContainsKey(parentId)) return Invalid("Parent topic not found.");
            if (!isNew && (parentId == topic.Id || AncestorsOf(parentId, byId).Contains(topic.Id)))
                return Invalid("A topic cannot be a subset of itself or its subtopics.");

            var depth = AncestorsOf(parentId, byId).Count + 2; // parent's ancestors + parent + topic
            var height = isNew ? 0 : SubtreeHeight(topic.Id, existing);
            if (depth + height > MaxDepth)
                return Invalid($"Topic tree max {MaxDepth} has a surface.");
        }
        else if (!isNew && 1 + SubtreeHeight(topic.Id, existing) > MaxDepth)
        {
            return Invalid($"Topic tree max {MaxDepth} has a surface.");
        }

        var duplicate = existing.Any(t => t.Id != topic.Id && t.ParentId == body.ParentId &&
                                          string.Equals(t.Title.Trim(), title, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
            return Results.Conflict(new { error = "There is a subject with the same title at the same level." });

        topic.ParentId = body.ParentId;
        topic.Title = title;
        topic.Description = description;
        topic.Keywords = keywords;
        topic.IsActive = body.IsActive ?? topic.IsActive;
        topic.SortOrder = body.SortOrder ?? (isNew
            ? existing.Where(t => t.ParentId == body.ParentId).Select(t => t.SortOrder + 10).DefaultIfEmpty(0).Max()
            : topic.SortOrder);
        return null;
    }

    private static List<Guid> AncestorsOf(Guid id, IReadOnlyDictionary<Guid, Topic> byId)
    {
        var ancestors = new List<Guid>();
        for (var current = byId.GetValueOrDefault(id)?.ParentId;
             current is { } p && byId.ContainsKey(p) && !ancestors.Contains(p);
             current = byId[p].ParentId)
            ancestors.Add(p);
        return ancestors;
    }

    private static int SubtreeHeight(Guid id, IReadOnlyList<Topic> all, int guard = 0)
    {
        if (guard > MaxDepth * 4) return 0;
        var children = all.Where(t => t.ParentId == id).ToList();
        return children.Count == 0 ? 0 : 1 + children.Max(c => SubtreeHeight(c.Id, all, guard + 1));
    }

    private static string NewCode(IReadOnlyList<Topic> existing)
    {
        string code;
        do code = "t-" + Guid.NewGuid().ToString("N")[..8];
        while (existing.Any(t => t.Code == code));
        return code;
    }

    private static IResult Invalid(string message) => Results.BadRequest(new { error = message });

    private static object Snapshot(Topic t) => new
    {
        t.Code, t.Title, t.Description, t.Keywords, parent_id = t.ParentId, t.SortOrder, is_active = t.IsActive
    };

    private static Task WriteAuditAsync(IAuditRepository audit, CxUser user, string action, Topic topic,
        object? before, object? after, CancellationToken ct) =>
        audit.WriteAsync(new AuditLog
        {
            TenantId = user.TenantId,
            ActorId = user.Subject,
            Action = action,
            ResourceType = "topic",
            ResourceId = topic.Code,
            Before = before is null ? null : CxJson.Serialize(before),
            After = after is null ? null : CxJson.Serialize(after is Topic t ? Snapshot(t) : after),
            IpAddress = user.IpAddress,
            OccurredAt = DateTimeOffset.UtcNow
        }, ct);
}

public sealed record TopicRequest(
    Guid? ParentId,
    string? Title,
    string? Description,
    List<string>? Keywords,
    int? SortOrder,
    bool? IsActive);

public sealed record TopicTreeView(int Revision, string? DecisionModel, bool CanManage, List<TopicView> Topics);

public sealed record TopicView(
    Guid Id,
    Guid? ParentId,
    string Code,
    string Title,
    string? Description,
    string[] Keywords,
    int SortOrder,
    bool IsActive,
    int UsageCount,
    string? UpdatedBy,
    DateTimeOffset UpdatedAt)
{
    public static TopicView From(Topic t, int usage) => new(t.Id, t.ParentId, t.Code, t.Title, t.Description,
        t.Keywords, t.SortOrder, t.IsActive, usage, t.UpdatedBy, t.UpdatedAt);
}
