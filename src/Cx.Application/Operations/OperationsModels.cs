using System.Text.Json;
using System.Text.Json.Serialization;
using Cx.Contracts;

namespace Cx.Application.Operations;

public sealed record BusinessCalendar
{
    public string TimeZone { get; init; } = "UTC";
    public int[] WorkingDays { get; init; } = [1, 2, 3, 4, 5];
    public string StartsAt { get; init; } = "09:00";
    public string EndsAt { get; init; } = "17:00";
    public string[] Holidays { get; init; } = [];
}

public sealed class WorkCase
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Reference { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Topic { get; set; }
    public string Priority { get; set; } = "medium";
    public string Status { get; set; } = "new";
    public Guid? TeamId { get; set; }
    public string? TeamName { get; set; }
    public Guid? AssigneeId { get; set; }
    public string? AssigneeName { get; set; }
    public Guid? ProblemId { get; set; }
    public string? ProblemTitle { get; set; }
    public Guid? SlaPolicyId { get; set; }
    [JsonIgnore] public string SlaCalendarJson { get; set; } = "{}";
    public BusinessCalendar Calendar => JsonSerializer.Deserialize<BusinessCalendar>(SlaCalendarJson, CxJson.Options) ?? new();
    public int ResponseMinutes { get; set; }
    public int ResolutionMinutes { get; set; }
    public DateTimeOffset ResponseDueAt { get; set; }
    public DateTimeOffset ResolutionDueAt { get; set; }
    public DateTimeOffset ResolutionStartedAt { get; set; }
    public DateTimeOffset? FirstResponseAt { get; set; }
    public DateTimeOffset? FirstResolvedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public double? ResponseBusinessMinutes { get; set; }
    public double? ResolutionBusinessMinutes { get; set; }
    public bool? ResponseWithinSla { get; set; }
    public bool? ResolutionWithinSla { get; set; }
    public DateTimeOffset? PauseStartedAt { get; set; }
    public double PausedMinutes { get; set; }
    public string ResolutionText { get; set; } = "";
    public int ReopenCount { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; }
    public Guid? WorkflowId { get; set; }
    [JsonIgnore] public string WorkflowJson { get; set; } = "{}";
    public WorkflowSnapshot Workflow => JsonSerializer.Deserialize<WorkflowSnapshot>(WorkflowJson, CxJson.Options) ?? new();
    public int StageIndex { get; set; } = -1;
    public string RoutingReason { get; set; } = "";
    public string? RoutingModel { get; set; }
    public bool ResponseOverdue => FirstResponseAt is null && Status is not ("resolved" or "closed") && DateTimeOffset.UtcNow > ResponseDueAt;
    public bool ResolutionOverdue => Status is not ("waiting_customer" or "resolved" or "closed") && DateTimeOffset.UtcNow > ResolutionDueAt;
}
public sealed class CaseActivity
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string Actor { get; set; } = "";
    public bool Public { get; set; }
    public string Text { get; set; } = "";
    public Guid? TeamId { get; set; }
    public string? StageTitle { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class CaseAttachment
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public string Filename { get; set; } = "";
    [JsonIgnore] public string ObjectKey { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool Public { get; set; }
    public string UploadedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed record CaseDetail(WorkCase Case, IReadOnlyList<CaseActivity> Activities, IReadOnlyList<CaseAttachment> Attachments, IReadOnlyList<Guid> FeedbackIds);
public sealed record CreateCaseInput(Guid Id, string CustomerId, string Title, string Description, string? Topic = null,
    string Priority = "medium", Guid? TeamId = null, Guid? AssigneeId = null, Guid? FeedbackId = null, Guid? ProblemId = null);
public sealed record UpdateCaseInput(int Version, string Status, Guid? TeamId, Guid? AssigneeId, Guid? ProblemId, string? ResolutionText, string? Priority = null, string? Comment = null);
public sealed record CaseMessageInput(Guid Id, string Text, bool Public = true);
public sealed record CustomerCaseAction(int Version, string Action, string? Comment);
public sealed record TeamInput(Guid Id, string Name, Guid? LeadId, Guid[] Members, bool Enabled = true, string Description = "");
public sealed class WorkTeam
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid? LeadId { get; set; }
    public bool Enabled { get; set; }
    public Guid[] Members { get; set; } = [];
}
public sealed record WorkflowStage(string Title, Guid? TeamId, string Description = "");
public sealed record WorkflowInput(Guid Id, string Name, string Description, string? Topic, WorkflowStage[] Stages, bool Enabled = true, int Version = 0);
public sealed class WorkflowDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Topic { get; set; }
    [JsonIgnore] public string StagesJson { get; set; } = "[]";
    public WorkflowStage[] Stages => JsonSerializer.Deserialize<WorkflowStage[]>(StagesJson, CxJson.Options) ?? [];
    public bool Enabled { get; set; }
    public int Version { get; set; }
}
public sealed record WorkflowSnapshot
{
    public string Name { get; init; } = "";
    public WorkflowStage[] Stages { get; init; } = [];
}
public sealed record CaseWorkflowInput(int Version, string Action, string Text = "");
public sealed record CaseRoute(Guid? WorkflowId, WorkflowSnapshot Workflow, string Reason, string? Model);
public sealed record WorkflowChange(Guid? WorkflowId, WorkflowSnapshot Workflow, int StageIndex, string Reason, string? Model, string Activity, Guid? StaffId, bool Manager);
public sealed record SlaPolicyInput(Guid Id, string Name, string Priority, int ResponseMinutes, int ResolutionMinutes, BusinessCalendar Calendar, bool Enabled = true);
public sealed class SlaPolicy
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Priority { get; set; } = "medium";
    public int ResponseMinutes { get; set; }
    public int ResolutionMinutes { get; set; }
    [JsonIgnore] public string CalendarJson { get; set; } = "{}";
    public BusinessCalendar Calendar => JsonSerializer.Deserialize<BusinessCalendar>(CalendarJson, CxJson.Options) ?? new();
    public bool Enabled { get; set; }
}
public sealed record RuleInput(Guid Id, string Name, string? Topic, string? Severity, string? Channel, int RepeatCount,
    string Priority, Guid? TeamId, Guid? AssigneeId, bool CreateCase = true, bool Notify = true, bool Enabled = false);
public sealed class AutomationRule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Topic { get; set; }
    public string? Severity { get; set; }
    public string? Channel { get; set; }
    public int RepeatCount { get; set; }
    public string Priority { get; set; } = "medium";
    public Guid? TeamId { get; set; }
    public Guid? AssigneeId { get; set; }
    public bool CreateCase { get; set; }
    public bool Notify { get; set; }
    public bool Enabled { get; set; }
}
public sealed record ProblemInput(Guid Id, string Title, string? Topic, string RootCause, string Improvement, string Status, Guid? OwnerId, int Version = 0);
public sealed class CommonProblem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? Topic { get; set; }
    public string RootCause { get; set; } = "";
    public string Improvement { get; set; } = "";
    public string Status { get; set; } = "";
    public Guid? OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FixedAt { get; set; }
    public int Version { get; set; }
    public long CaseCount { get; set; }
    public long AffectedCustomers { get; set; }
    public long BeforeCount { get; set; }
    public long AfterCount { get; set; }
    public int ComparisonDays { get; set; }
}
public sealed record CampaignInput(Guid Id, string Name, string Question, string Metric, string Trigger, string? EventName,
    string? Segment, int CooldownDays = 30, int ReminderDays = 3, int ExpiresDays = 14, bool Enabled = false);
public sealed class SurveyCampaign
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Question { get; set; } = "";
    public string Metric { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string? EventName { get; set; }
    public string? Segment { get; set; }
    public int CooldownDays { get; set; }
    public int ReminderDays { get; set; }
    public int ExpiresDays { get; set; }
    public bool Enabled { get; set; }
    public long Invitations { get; set; }
    public long Responses { get; set; }
    public long Declined { get; set; }
    public long Expired { get; set; }
    public decimal? AverageScore { get; set; }
}
public sealed class SurveyInvitation
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public string CampaignName { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public Guid? CaseId { get; set; }
    public string Question { get; set; } = "";
    public string Metric { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public DateTimeOffset? DeclinedAt { get; set; }
    public int? Score { get; set; }
    public string? Comment { get; set; }
}
public sealed record SurveyAnswerInput(int Score, string? Comment);
public sealed class JourneyExperience
{
    public string Goal { get; init; } = "";
    public string Actions { get; init; } = "";
    public string Touchpoints { get; init; } = "";
    public string Thoughts { get; init; } = "";
    public string Emotion { get; init; } = "unknown";
    public string Feeling { get; init; } = "";
    public string PainPoints { get; init; } = "";
    public string Opportunities { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string EvidenceStatus { get; init; } = "hypothesis";
    public string SuccessMetric { get; init; } = "";
    public string Owner { get; init; } = "";
}
public sealed record JourneyStageInput(string Title, string[] EventNames)
{
    public JourneyExperience Experience { get; init; } = new();
}
public sealed record JourneyInput(Guid Id, string Name, int WindowDays, JourneyStageInput[] Stages, bool Enabled = true)
{
    public string Persona { get; init; } = "";
    public string Scenario { get; init; } = "";
    public string Goal { get; init; } = "";
    public string MapType { get; init; } = "current_state";
}
public sealed class JourneyDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int WindowDays { get; set; }
    public bool Enabled { get; set; }
    public JourneyStageInput[] Stages { get; set; } = [];
    public string Persona { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string Goal { get; set; } = "";
    public string MapType { get; set; } = "current_state";
}
public sealed record JourneyStageResult(string Title, long Reached, long CompletedNext, long Pending, long Dropped, decimal? Csat, long Complaints);
public sealed record JourneyResult(Guid Id, string Name, int WindowDays, IReadOnlyList<JourneyStageResult> Stages);
public sealed class WorkNotification
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Link { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
public sealed class FollowUpCustomer
{
    public string CustomerId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Segment { get; set; }
    public long Complaints { get; set; }
    public long OpenCases { get; set; }
    public long OverdueCases { get; set; }
    public decimal? RecentCsat { get; set; }
    public decimal? PreviousCsat { get; set; }
    public decimal? RecentCes { get; set; }
    public long NegativeFeedback { get; set; }
    public int FollowUpScore { get; set; }
    public string[] Reasons { get; set; } = [];
}
public sealed class ServiceOutcome
{
    public string Name { get; set; } = "";
    public long Total { get; set; }
    public long Resolved { get; set; }
    public long Overdue { get; set; }
    public long Reopened { get; set; }
    public long ResponseMeasured { get; set; }
    public long ResolutionMeasured { get; set; }
    public long ResponseWithinSla { get; set; }
    public long ResolutionWithinSla { get; set; }
    public double? AverageResponseMinutes { get; set; }
    public double? AverageResolutionMinutes { get; set; }
    public decimal? AftercareCsat { get; set; }
    public long AftercareResponses { get; set; }
}
public sealed record OperationsReport(ServiceOutcome Summary, IReadOnlyList<ServiceOutcome> Teams, IReadOnlyList<ServiceOutcome> Topics);

public sealed class OperationsException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
