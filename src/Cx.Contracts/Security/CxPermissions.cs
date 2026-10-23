namespace Cx.Contracts.Security;

/// <summary>
/// Explicit permission constants (spec 27). The backend enforces these;
/// hiding UI elements is never sufficient.
/// </summary>
public static class CxPermissions
{
    public const string CustomerRead = "cx.customer.read";
    public const string EventRead = "cx.event.read";
    public const string FeedbackManage = "cx.feedback.manage";
    public const string SupportRead = "cx.support.read";
    public const string VoiceListen = "cx.voice.listen";
    public const string VoiceTranscriptRead = "cx.voice.transcript.read";
    public const string VoiceTranscriptEdit = "cx.voice.transcript.edit";
    public const string AiReview = "cx.ai.review";
    public const string AiReprocess = "cx.ai.reprocess";
    public const string TaxonomyManage = "cx.taxonomy.manage";
    public const string PipelineManage = "cx.pipeline.manage";
    public const string AuditRead = "cx.audit.read";
    public const string Export = "cx.export";
    public const string PiiRead = "cx.pii.read";
    public const string Admin = "cx.admin";
    /// <summary>Machine-to-machine permission used by producers of the ingestion API.</summary>
    public const string EventIngest = "cx.event.ingest";
    public const string WorkRead = "cx.work.read";
    public const string WorkManage = "cx.work.manage";
    public const string OperationsConfigure = "cx.operations.configure";
    public const string TeamManage = "cx.team.manage";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        CustomerRead, EventRead, FeedbackManage, SupportRead,
        VoiceListen, VoiceTranscriptRead, VoiceTranscriptEdit,
        AiReview, AiReprocess, TaxonomyManage, PipelineManage,
        AuditRead, Export, PiiRead, Admin, EventIngest, WorkRead, WorkManage, OperationsConfigure, TeamManage
    };
}

/// <summary>Role presets from the panel proposal (2). Roles map to permission sets.</summary>
public static class CxRoles
{
    public const string CxAnalyst = "cx_analyst";
    public const string CxLead = "cx_lead";
    public const string SupportSupervisor = "support_supervisor";
    public const string DataAiOperator = "data_ai_operator";
    public const string Admin = "admin";
    public const string Producer = "producer";
    public const string Customer = "customer";

    public static IReadOnlyDictionary<string, string[]> Defaults { get; } = new Dictionary<string, string[]>
    {
        [CxAnalyst] = new[]
        {
            CxPermissions.CustomerRead, CxPermissions.EventRead, CxPermissions.SupportRead,
            CxPermissions.FeedbackManage, CxPermissions.AiReview, CxPermissions.VoiceTranscriptRead,
            CxPermissions.WorkRead, CxPermissions.WorkManage
        },
        [CxLead] = new[]
        {
            CxPermissions.CustomerRead, CxPermissions.EventRead, CxPermissions.SupportRead,
            CxPermissions.FeedbackManage, CxPermissions.AiReview, CxPermissions.AiReprocess,
            CxPermissions.VoiceListen, CxPermissions.VoiceTranscriptRead, CxPermissions.TaxonomyManage,
            CxPermissions.Export, CxPermissions.AuditRead, CxPermissions.WorkRead, CxPermissions.WorkManage, CxPermissions.OperationsConfigure
        },
        [SupportSupervisor] = new[]
        {
            CxPermissions.CustomerRead, CxPermissions.EventRead, CxPermissions.SupportRead,
            CxPermissions.VoiceListen, CxPermissions.VoiceTranscriptRead, CxPermissions.AiReview,
            CxPermissions.WorkRead, CxPermissions.WorkManage, CxPermissions.OperationsConfigure
        },
        [DataAiOperator] = new[]
        {
            CxPermissions.EventRead, CxPermissions.PipelineManage, CxPermissions.AiReprocess,
            CxPermissions.AuditRead
        },
        [Admin] = CxPermissions.All.ToArray(),
        [Customer] = Array.Empty<string>(),
        [Producer] = new[] { CxPermissions.EventIngest }
    };
}
