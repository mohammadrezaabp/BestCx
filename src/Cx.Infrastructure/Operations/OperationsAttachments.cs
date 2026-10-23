using Cx.Application.Operations;
using Dapper;

namespace Cx.Infrastructure.Operations;

public sealed partial class OperationsStore
{
    public async Task<CaseAttachment?> AttachmentAsync(string tenant, Guid caseId, Guid id, string? customer, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<CaseAttachment>(new CommandDefinition("""
            SELECT a.* FROM cx_case_attachment a JOIN cx_case w ON w.tenant_id=a.tenant_id AND w.id=a.case_id
            WHERE a.tenant_id=@tenant AND a.case_id=@caseId AND a.id=@id AND (@customer IS NULL OR (w.customer_id=@customer AND a.public))
            """, new { tenant, caseId, id, customer }, cancellationToken: ct));
    }
    public async Task SaveAttachmentAsync(string tenant, string actor, Guid caseId, CaseAttachment attachment, string? customer, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var item = await db.QuerySingleOrDefaultAsync<WorkCase>(new CommandDefinition("""
            SELECT *,sla_calendar::text AS sla_calendar_json FROM cx_case WHERE tenant_id=@tenant AND id=@caseId
              AND (@customer IS NULL OR customer_id=@customer) FOR UPDATE
            """, new { tenant, caseId, customer }, tx, cancellationToken: ct)) ?? throw new OperationsException("File not found.", 404);
        if (customer is not null && (item.Status is "resolved" or "closed")) throw new OperationsException("First, open the file.", 409);
        attachment.CreatedAt = Now;
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_case_attachment(id,tenant_id,case_id,filename,object_key,content_type,size_bytes,public,uploaded_by,created_at)
            VALUES(@id,@tenant,@caseId,@filename,@objectKey,@contentType,@sizeBytes,@public,@actor,@now)
            """, new { attachment.Id, tenant, caseId, attachment.Filename, attachment.ObjectKey, attachment.ContentType, attachment.SizeBytes,
                Public = customer is not null || attachment.Public, actor, now = Now }, tx, cancellationToken: ct));
        await ActivityAsync(db, tx, tenant, caseId, actor, "attachment", attachment.Filename, customer is not null || attachment.Public, Now, ct);
        if (customer is not null && item.PauseStartedAt is not null)
        {
            var paused = BusinessClock.Between(item.PauseStartedAt.Value, Now, item.Calendar);
            item.ResolutionDueAt = BusinessClock.Add(item.ResolutionDueAt, paused, item.Calendar);
            item.PausedMinutes += paused; item.PauseStartedAt = null; item.Status = "in_progress";
        }
        if (customer is null && attachment.Public && item.FirstResponseAt is null)
        {
            item.FirstResponseAt = Now; item.ResponseBusinessMinutes = BusinessClock.Between(item.CreatedAt, Now, item.Calendar);
            item.ResponseWithinSla = Now <= item.ResponseDueAt;
        }
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE cx_case SET first_response_at=@FirstResponseAt,response_business_minutes=@ResponseBusinessMinutes,
              status=@Status,resolution_due_at=@ResolutionDueAt,paused_minutes=@PausedMinutes,pause_started_at=@PauseStartedAt,
              response_within_sla=@ResponseWithinSla,version=version+1,updated_at=@now WHERE tenant_id=@tenant AND id=@caseId
            """, new { tenant, caseId, item.FirstResponseAt, item.ResponseBusinessMinutes, item.ResponseWithinSla,
                item.Status, item.ResolutionDueAt, item.PausedMinutes, item.PauseStartedAt, now = Now }, tx, cancellationToken: ct));
        if (customer is null && attachment.Public)
            await NotifyAsync(db, tx, tenant, "customer", item.CustomerId, "New attachment for your request", item.Reference,
                $"/tracking/{caseId}", $"attachment:{attachment.Id}", Now, ct);
        if (customer is not null && item.AssigneeId is not null)
            await NotifyAsync(db, tx, tenant, "staff", item.AssigneeId.Value.ToString(), "The customer sent an attachment", item.Reference,
                $"/complaints/cases/{caseId}", $"attachment:{attachment.Id}", Now, ct);
        await tx.CommitAsync(ct);
    }
    public async Task<(IReadOnlyList<WorkCase> Items, long Total, int Page)> PortalCasesAsync(string tenant, string customer, int page, CancellationToken ct)
    {
        page = Math.Clamp(page, 1, 100000);
        await using var db = await dataSource.OpenAsync(ct);
        var items = (await db.QueryAsync<WorkCase>(new CommandDefinition(CaseSelect +
            " WHERE w.tenant_id=@tenant AND w.customer_id=@customer ORDER BY w.updated_at DESC,w.id LIMIT 20 OFFSET @offset", new { tenant, customer, offset = (page - 1) * 20 }, cancellationToken: ct))).AsList();
        var total = await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM cx_case WHERE tenant_id=@tenant AND customer_id=@customer", new { tenant, customer }, cancellationToken: ct));
        return (items, total, page);
    }
    public async Task<object> PortalFeedbackAsync(string tenant, string customer, int page, CancellationToken ct)
    {
        page = Math.Clamp(page, 1, 100000);
        await using var db = await dataSource.OpenAsync(ct);
        var items = (await db.QueryAsync(new CommandDefinition("""
            SELECT f.id,f.comment,f.feedback_type,f.submitted_at,f.status,x.case_id FROM feedback f
            LEFT JOIN cx_case_feedback x ON x.tenant_id=f.tenant_id AND x.feedback_id=f.id
            WHERE f.tenant_id=@tenant AND f.customer_id=@customer ORDER BY f.submitted_at DESC,f.id LIMIT 20 OFFSET @offset
            """, new { tenant, customer, offset = (page - 1) * 20 }, cancellationToken: ct))).Select(row => (object)row).ToList();
        var total = await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM feedback WHERE tenant_id=@tenant AND customer_id=@customer", new { tenant, customer }, cancellationToken: ct));
        return new { items, total, page, page_size = 20 };
    }
}
