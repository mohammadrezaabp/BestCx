using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Operations;
using Cx.Contracts;
using Cx.Contracts.Events;
using Cx.Contracts.Messaging;
using Dapper;
using Npgsql;

namespace Cx.Infrastructure.Operations;

public sealed partial class OperationsStore
{
    public async Task<IReadOnlyList<SurveyCampaign>> CampaignsAsync(string tenant, CancellationToken ct)
    {
        await EnsureDefaultsAsync(tenant, ct);
        await using var db = await dataSource.OpenAsync(ct);
        return (await db.QueryAsync<SurveyCampaign>(new CommandDefinition("""
            SELECT c.*,count(i.id) AS invitations,count(i.answered_at) AS responses,count(i.declined_at) AS declined,
              count(i.id) FILTER(WHERE i.answered_at IS NULL AND i.declined_at IS NULL AND i.expires_at<@now) AS expired,
              avg(i.score) AS average_score
            FROM cx_survey_campaign c LEFT JOIN cx_survey_invitation i ON i.tenant_id=c.tenant_id AND i.campaign_id=c.id
            WHERE c.tenant_id=@tenant GROUP BY c.id ORDER BY c.name
            """, new { tenant, now = Now }, cancellationToken: ct))).AsList();
    }
    public async Task SaveCampaignAsync(string tenant, string actor, CampaignInput input, CancellationToken ct)
    {
        Text(input.Name, 128, "Name of the campaign"); Text(input.Question, 512, "Question"); Text(input.EventName, 128, "Event", input.Trigger == "event"); Text(input.Segment, 128, "Customer group", false);
        if (input.Id == Guid.Empty || !new[] { "csat", "ces", "nps" }.Contains(input.Metric) || !new[] { "manual", "case_resolved", "event" }.Contains(input.Trigger) ||
            input.CooldownDays is < 1 or > 365 || input.ReminderDays is < 1 or > 30 || input.ExpiresDays <= input.ReminderDays || input.ExpiresDays > 90)
            throw new OperationsException("Campaign settings are not valid; The response time should be longer than the reminder interval.");
        await using var db = await dataSource.OpenAsync(ct);
        var changed = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_survey_campaign(id,tenant_id,name,question,metric,trigger,event_name,segment,cooldown_days,reminder_days,expires_days,enabled)
            VALUES(@id,@tenant,@name,@question,@metric,@trigger,@eventName,@segment,@cooldownDays,@reminderDays,@expiresDays,@enabled)
            ON CONFLICT(id) DO UPDATE SET name=@name,question=@question,metric=@metric,trigger=@trigger,event_name=@eventName,segment=@segment,
              cooldown_days=@cooldownDays,reminder_days=@reminderDays,expires_days=@expiresDays,
              created_at=CASE WHEN @enabled AND NOT cx_survey_campaign.enabled THEN now() ELSE cx_survey_campaign.created_at END,
              enabled=@enabled WHERE cx_survey_campaign.tenant_id=@tenant
            """, new { tenant, input.Id, name = input.Name.Trim(), question = input.Question.Trim(), input.Metric, input.Trigger, input.EventName,
                input.Segment, input.CooldownDays, input.ReminderDays, input.ExpiresDays, input.Enabled }, cancellationToken: ct));
        if (changed == 0) throw new OperationsException("Campaign not found.", 404);
        await AuditConfigurationAsync(tenant, actor, "cx_survey_campaign", input.Id, input, ct);
    }

    private static async Task<bool> InviteAsync(NpgsqlConnection db, NpgsqlTransaction tx, string tenant, SurveyCampaign campaign,
        string customer, string source, Guid? caseId, DateTimeOffset now, CancellationToken ct)
    {
        await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new { key = $"survey:{tenant}:{customer}" }, tx, cancellationToken: ct));
        var eligible = await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM customer_identity c WHERE tenant_id=@tenant AND customer_id=@customer AND merged_into_id IS NULL
              AND (@segment IS NULL OR c.segment=@segment)
              AND NOT EXISTS(SELECT 1 FROM cx_survey_invitation i WHERE i.tenant_id=@tenant AND i.customer_id=@customer
                AND (i.sent_at>@now-interval '1 day' OR (i.campaign_id=@campaign AND i.sent_at>@now-@cooldown*interval '1 day'))))
            """, new { tenant, customer, campaign = campaign.Id, campaign.Segment, now, cooldown = campaign.CooldownDays }, tx, cancellationToken: ct));
        if (!eligible) return false;
        var invitation = Guid.NewGuid();
        var count = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_survey_invitation(id,tenant_id,campaign_id,customer_id,source_key,case_id,metric,question,sent_at,expires_at)
            VALUES(@invitation,@tenant,@campaign,@customer,@source,@caseId,@metric,@question,@now,@expires)
            ON CONFLICT(tenant_id,campaign_id,source_key) DO NOTHING
            """, new { invitation, tenant, campaign = campaign.Id, customer, source, caseId, campaign.Metric, campaign.Question, now, expires = now.AddDays(campaign.ExpiresDays) }, tx, cancellationToken: ct));
        if (count == 0) return false;
        await NotifyAsync(db, tx, tenant, "customer", customer, campaign.Name, campaign.Question, $"/surveys/{invitation}", $"survey:{invitation}", now, ct);
        return true;
    }
    private static async Task InviteAftercareAsync(NpgsqlConnection db, NpgsqlTransaction tx, string tenant, string customer,
        Guid caseId, int cycle, DateTimeOffset now, CancellationToken ct)
    {
        var campaigns = await db.QueryAsync<SurveyCampaign>(new CommandDefinition(
            "SELECT * FROM cx_survey_campaign WHERE tenant_id=@tenant AND trigger='case_resolved' AND enabled ORDER BY created_at,id", new { tenant }, tx, cancellationToken: ct));
        foreach (var campaign in campaigns) await InviteAsync(db, tx, tenant, campaign, customer, $"case:{caseId}:{cycle}", caseId, now, ct);
    }
    public async Task<object> DispatchCampaignAsync(string tenant, string actor, Guid id, string[] customers, CancellationToken ct)
    {
        if (customers is not { Length: >= 1 and <= 200 } || customers.Any(customer => string.IsNullOrWhiteSpace(customer) || customer.Length > 128)) throw new OperationsException("Select 1 to 200 customers.");
        await using var db = await dataSource.OpenAsync(ct);
        var campaign = await db.QuerySingleOrDefaultAsync<SurveyCampaign>(new CommandDefinition(
            "SELECT * FROM cx_survey_campaign WHERE tenant_id=@tenant AND id=@id AND enabled", new { tenant, id }, cancellationToken: ct))
            ?? throw new OperationsException("No active campaign found.", 404);
        var invited = 0;
        foreach (var customer in customers.Distinct())
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            if (await InviteAsync(db, tx, tenant, campaign, customer, $"manual:{customer}:{Now:yyyy-MM-dd}", null, Now, ct)) invited++;
            await tx.CommitAsync(ct);
        }
        await AuditConfigurationAsync(tenant, actor, "cx_survey_dispatch", id, new { requested = customers.Length, invited }, ct);
        return new { invited, skipped = customers.Distinct().Count() - invited };
    }
    public async Task<IReadOnlyList<SurveyInvitation>> InvitationsAsync(string tenant, string customer, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return (await db.QueryAsync<SurveyInvitation>(new CommandDefinition("""
            SELECT i.*,c.name AS campaign_name FROM cx_survey_invitation i JOIN cx_survey_campaign c ON c.tenant_id=i.tenant_id AND c.id=i.campaign_id
            WHERE i.tenant_id=@tenant AND i.customer_id=@customer ORDER BY i.sent_at DESC LIMIT 100
            """, new { tenant, customer }, cancellationToken: ct))).AsList();
    }
    public async Task<SurveyInvitation?> InvitationAsync(string tenant, string customer, Guid id, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<SurveyInvitation>(new CommandDefinition("""
            SELECT i.*,c.name AS campaign_name FROM cx_survey_invitation i JOIN cx_survey_campaign c ON c.tenant_id=i.tenant_id AND c.id=i.campaign_id
            WHERE i.tenant_id=@tenant AND i.customer_id=@customer AND i.id=@id
            """, new { tenant, customer, id }, cancellationToken: ct));
    }
    public async Task AnswerAsync(string tenant, string customer, Guid id, SurveyAnswerInput input, CancellationToken ct)
    {
        Text(input.Comment, 12000, "Explanation", false);
        await using var db = await dataSource.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var invitation = await db.QuerySingleOrDefaultAsync<SurveyInvitation>(new CommandDefinition(
            "SELECT * FROM cx_survey_invitation WHERE tenant_id=@tenant AND customer_id=@customer AND id=@id FOR UPDATE", new { tenant, customer, id }, tx, cancellationToken: ct))
            ?? throw new OperationsException("Survey invitation not found.", 404);
        if (invitation.AnsweredAt is not null)
        {
            if (invitation.Score != input.Score || invitation.Comment != input.Comment?.Trim()) throw new OperationsException("This response has already been filed.", 409);
            await tx.CommitAsync(ct); return;
        }
        if (invitation.ExpiresAt <= Now || invitation.DeclinedAt is not null) throw new OperationsException("The deadline for responding to this invitation has ended.", 409);
        var minimum = invitation.Metric == "nps" ? 0 : 1;
        var maximum = invitation.Metric switch { "csat" => 5, "ces" => 7, _ => 10 };
        if (input.Score < minimum || input.Score > maximum) throw new OperationsException("Points are not valid.");
        var now = Now;
        var feedback = StableId($"survey-feedback:{id}");
        var eventId = $"survey-{id:N}";
        var eventName = invitation.Metric switch { "csat" => EventNames.FeedbackCsatSubmitted, "nps" => EventNames.FeedbackNpsSubmitted, _ => EventNames.FeedbackSubmitted };
        var comment = $"{invitation.Question}\nRating: {input.Score}\n{input.Comment?.Trim()}";
        var evt = new CanonicalEvent {
            EventId = eventId, TenantId = tenant, Customer = new CustomerRef { CustomerId = customer }, EventName = eventName,
            EventCategory = EventCategories.Feedback, OccurredAt = now, ReceivedAt = now,
            Source = new SourceRef { Type = "web", Service = "cx-survey-campaign", Channel = "web" },
            Privacy = new PrivacyRef { ContainsPii = true, Classification = "confidential" },
            Metadata = new JsonObject { ["external_id"] = id.ToString(), ["campaign_id"] = invitation.CampaignId.ToString(), ["comment"] = comment, [$"{invitation.Metric}_score"] = input.Score }
        };
        var record = await db.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO cx_event(event_id,tenant_id,customer_id,event_name,event_category,schema_version,occurred_at,received_at,source_type,source_service,payload)
            VALUES(@eventId,@tenant,@customer,@eventName,'feedback',@schema,@now,@now,'web','cx-survey-campaign',@payload::jsonb)
            RETURNING id
            """, new { eventId, tenant, customer, eventName, schema = evt.SchemaVersion, now, payload = Json(evt) }, tx, cancellationToken: ct));
        var message = new PersistedEventMessage { EventId = eventId, TenantId = tenant, CustomerId = customer, EventName = eventName,
            EventCategory = EventCategories.Feedback, OccurredAt = now, PersistedAt = now, SourceRecordId = record, Envelope = JsonSerializer.SerializeToNode(evt, CxJson.Options)!.AsObject() };
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_outbox(id,aggregate_type,aggregate_id,event_type,topic,partition_key,payload)
            VALUES(@outbox,'cx_event',@eventId,@eventName,@topic,@partition,@payload::jsonb)
            """, new { outbox = StableId($"survey-outbox:{id}"), eventId, eventName, topic = messaging.Value.Topics.PersistedEvents, partition = evt.PartitionKey(), payload = Json(message) }, tx, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO feedback(id,tenant_id,event_id,provider,external_id,customer_id,feedback_type,csat_score,ces_score,nps_score,comment,channel,status,submitted_at)
            VALUES(@feedback,@tenant,@eventId,'cx-survey-campaign',@external,@customer,@type,@csat,@ces,@nps,@comment,'web','new',@now)
            """, new { feedback, tenant, eventId, external = id.ToString(), customer, type = invitation.Metric == "nps" ? "nps" : invitation.Metric == "csat" ? "csat" : "generic",
                csat = invitation.Metric == "csat" ? (int?)input.Score : null, ces = invitation.Metric == "ces" ? (int?)input.Score : null, nps = invitation.Metric == "nps" ? (int?)input.Score : null, comment, now }, tx, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition("UPDATE cx_survey_invitation SET answered_at=@now,score=@score,comment=@comment,feedback_id=@feedback WHERE tenant_id=@tenant AND id=@id",
            new { tenant, id, now, input.Score, comment = input.Comment?.Trim(), feedback }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
    public async Task DeclineAsync(string tenant, string customer, Guid id, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        var count = await db.ExecuteAsync(new CommandDefinition("""
            UPDATE cx_survey_invitation SET declined_at=COALESCE(declined_at,@now)
            WHERE tenant_id=@tenant AND customer_id=@customer AND id=@id AND answered_at IS NULL
            """, new { tenant, customer, id, now = Now }, cancellationToken: ct));
        if (count == 0) throw new OperationsException("Unable to cancel invitation found.", 404);
    }
}
