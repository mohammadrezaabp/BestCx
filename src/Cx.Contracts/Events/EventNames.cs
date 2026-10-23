namespace Cx.Contracts.Events;

/// <summary>
/// Initial event catalog (spec 7 / technical design 7).
/// Naming pattern: domain.entity-or-action.state. IDs are never part of an event name.
/// </summary>
public static class EventNames
{
    // user / session
    public const string UserSessionStarted = "user.session.started";
    public const string UserSessionEnded = "user.session.ended";
    public const string UserLoginSucceeded = "user.login.succeeded";
    public const string UserLoginFailed = "user.login.failed";

    // product interaction
    public const string PageViewed = "page.viewed";
    public const string ButtonClicked = "button.clicked";
    public const string FormStarted = "form.started";
    public const string FormSubmitted = "form.submitted";
    public const string FormFailed = "form.failed";
    public const string ProductServiceViewed = "product.service.viewed";
    public const string ProductServiceActivated = "product.service.activated";

    // api
    public const string ApiRequestSucceeded = "api.request.succeeded";
    public const string ApiRequestFailed = "api.request.failed";
    public const string ApiTimeout = "api.timeout";
    public const string ApiRateLimitHit = "api.rate_limit.hit";

    // feedback
    public const string FeedbackSubmitted = "feedback.submitted";
    public const string FeedbackCsatSubmitted = "feedback.csat.submitted";
    public const string FeedbackNpsSubmitted = "feedback.nps.submitted";
    public const string FeedbackRatingSubmitted = "feedback.rating.submitted";

    // support
    public const string SupportTicketCreated = "support.ticket.created";
    public const string SupportTicketAssigned = "support.ticket.assigned";
    public const string SupportTicketReplied = "support.ticket.replied";
    public const string SupportTicketEscalated = "support.ticket.escalated";
    public const string SupportTicketResolved = "support.ticket.resolved";
    public const string SupportTicketReopened = "support.ticket.reopened";

    // voice
    public const string VoiceCallStarted = "voice.call.started";
    public const string VoiceCallAnswered = "voice.call.answered";
    public const string VoiceCallEnded = "voice.call.ended";
    public const string VoiceCallDropped = "voice.call.dropped";
    public const string VoiceRecordingReady = "voice.recording.ready";
    public const string VoiceTranscriptReady = "voice.transcript.ready";
    public const string VoiceAnalysisCompleted = "voice.analysis.completed";

    private static readonly Dictionary<string, string> CategoryByName = new(StringComparer.Ordinal)
    {
        [UserSessionStarted] = EventCategories.UserSession,
        [UserSessionEnded] = EventCategories.UserSession,
        [UserLoginSucceeded] = EventCategories.UserSession,
        [UserLoginFailed] = EventCategories.UserSession,

        [PageViewed] = EventCategories.ProductInteraction,
        [ButtonClicked] = EventCategories.ProductInteraction,
        [FormStarted] = EventCategories.ProductInteraction,
        [FormSubmitted] = EventCategories.ProductInteraction,
        [FormFailed] = EventCategories.ProductInteraction,
        [ProductServiceViewed] = EventCategories.ProductInteraction,
        [ProductServiceActivated] = EventCategories.ProductInteraction,

        [ApiRequestSucceeded] = EventCategories.Api,
        [ApiRequestFailed] = EventCategories.Api,
        [ApiTimeout] = EventCategories.Api,
        [ApiRateLimitHit] = EventCategories.Api,

        [FeedbackSubmitted] = EventCategories.Feedback,
        [FeedbackCsatSubmitted] = EventCategories.Feedback,
        [FeedbackNpsSubmitted] = EventCategories.Feedback,
        [FeedbackRatingSubmitted] = EventCategories.Feedback,

        [SupportTicketCreated] = EventCategories.Support,
        [SupportTicketAssigned] = EventCategories.Support,
        [SupportTicketReplied] = EventCategories.Support,
        [SupportTicketEscalated] = EventCategories.Support,
        [SupportTicketResolved] = EventCategories.Support,
        [SupportTicketReopened] = EventCategories.Support,

        [VoiceCallStarted] = EventCategories.Voice,
        [VoiceCallAnswered] = EventCategories.Voice,
        [VoiceCallEnded] = EventCategories.Voice,
        [VoiceCallDropped] = EventCategories.Voice,
        [VoiceRecordingReady] = EventCategories.Voice,
        [VoiceTranscriptReady] = EventCategories.Voice,
        [VoiceAnalysisCompleted] = EventCategories.Ai
    };

    public static IReadOnlyCollection<string> Known => CategoryByName.Keys;

    public static bool IsKnown(string? eventName) =>
        eventName is not null && CategoryByName.ContainsKey(eventName);

    public static string? CategoryOf(string eventName) =>
        CategoryByName.TryGetValue(eventName, out var c) ? c : null;

    /// <summary>Rejects dynamic values embedded in event names (technical design 6.2).</summary>
    public static bool IsWellFormed(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return false;
        if (eventName.Length > 160) return false;
        var segments = eventName.Split('.');
        if (segments.Length is < 2 or > 4) return false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0) return false;
            foreach (var ch in segment)
            {
                if (!(char.IsAsciiLetterLower(ch) || ch == '_')) return false;
            }
        }
        return true;
    }
}
