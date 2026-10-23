namespace Cx.Host;

/// <summary>
/// The single image runs one or more roles, selected with CX_ROLES (comma separated)
/// or Roles in configuration. Default is "all": every API and worker in one process.
///
/// The module boundaries from the implementation spec (3) are preserved as separate
/// projects, so the same build can later be deployed as several containers - each one
/// started with a narrower CX_ROLES - without any code change.
/// </summary>
public sealed class CxRoleSet
{
    public const string IngestionApi = "ingestion";
    public const string QueryApi = "query";
    public const string AdminApi = "admin";
    public const string Web = "web";
    public const string PersistenceWorker = "persistence";
    public const string OutboxRelay = "outbox";
    public const string VoiceWorker = "voice";
    public const string AiOrchestrator = "orchestrator";
    public const string AiWorker = "ai";
    public const string Projections = "projections";

    private static readonly string[] Everything =
    {
        IngestionApi, QueryApi, AdminApi, Web,
        PersistenceWorker, OutboxRelay, VoiceWorker,
        AiOrchestrator, AiWorker, Projections
    };

    private readonly HashSet<string> _roles;

    private CxRoleSet(IEnumerable<string> roles) =>
        _roles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);

    public static CxRoleSet Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return new CxRoleSet(Everything);

        var requested = value
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var unknown = requested.Where(r => !Everything.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"Unknown CX role(s): {string.Join(", ", unknown)}. Valid roles: {string.Join(", ", Everything)}, or 'all'.");
        }

        return new CxRoleSet(requested);
    }

    public bool Has(string role) => _roles.Contains(role);

    /// <summary>True when any HTTP surface is enabled and the web server is needed.</summary>
    public bool HasHttp => Has(IngestionApi) || Has(QueryApi) || Has(AdminApi) || Has(Web);

    public override string ToString() =>
        string.Join(", ", Everything.Where(_roles.Contains));
}
