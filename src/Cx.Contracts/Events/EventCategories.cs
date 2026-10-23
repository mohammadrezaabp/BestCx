namespace Cx.Contracts.Events;

public static class EventCategories
{
    public const string UserSession = "user_session";
    public const string ProductInteraction = "product_interaction";
    public const string Api = "api";
    public const string Feedback = "feedback";
    public const string Support = "support";
    public const string Voice = "voice";
    public const string Ai = "ai";
    public const string System = "system";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        UserSession, ProductInteraction, Api, Feedback, Support, Voice, Ai, System
    };
}
