using Cx.Infrastructure.Security;
using Cx.Infrastructure.Operations;
using System.Text.Json;
using Cx.Admin.Api;
using Cx.Ai.Orchestrator;
using Cx.Ai.Worker;
using Cx.Application.Abstractions;
using Cx.Application.Observability;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Host;
using Cx.Infrastructure;
using Cx.Infrastructure.Database;
using Cx.Infrastructure.Messaging;
using Cx.Ingestion.Api;
using Cx.Integrations.Support;
using Cx.Integrations.Voip;
using Cx.Persistence.Worker;
using Cx.Query.Api;
using Cx.Voice.Worker;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
builder.Configuration.AddEnvironmentVariables("CX_");

var roles = CxRoleSet.Parse(builder.Configuration["Roles"] ?? Environment.GetEnvironmentVariable("CX_ROLES"));
builder.Services.AddSingleton(roles);

// Structured logs everywhere (spec 37.15). Secrets and payloads are never logged.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "cx-platform")
    .Enrich.WithProperty("roles", roles.ToString())
    .WriteTo.Console(context.HostingEnvironment.IsDevelopment()
        ? new Serilog.Formatting.Display.MessageTemplateTextFormatter(
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        : new CompactJsonFormatter()));

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------
builder.Services.AddCxInfrastructure(builder.Configuration);
builder.Services.AddCxAuth(builder.Configuration);
builder.Services.AddCxTelemetry(builder.Configuration, "cx-platform");
builder.Services.AddCxRateLimiting();
builder.Services.Configure<CustomerPortalOptions>(builder.Configuration.GetSection("CustomerPortal"));

builder.Services.Configure<OutboxRelayOptions>(builder.Configuration.GetSection(OutboxRelayOptions.Section));
builder.Services.Configure<VoipOptions>(builder.Configuration.GetSection(VoipOptions.Section));

// The connector follows redirects itself so every hop is checked against the trusted hosts.
builder.Services.AddHttpClient<IVoipConnector, HttpVoipConnector>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<RecordingIngestService>();

builder.Services.AddHostedService<StartupTasks>();
if (roles.Has(CxRoleSet.QueryApi)) builder.Services.AddHostedService<OperationsWorker>();

// Only the requested roles are started; the rest of the image stays dormant.
if (roles.Has(CxRoleSet.PersistenceWorker)) builder.Services.AddHostedService<PersistenceWorker>();
if (roles.Has(CxRoleSet.OutboxRelay)) builder.Services.AddHostedService<OutboxRelayService>();
if (roles.Has(CxRoleSet.Projections)) builder.Services.AddHostedService<InboxProjectionWorker>();
if (roles.Has(CxRoleSet.VoiceWorker))
{
    builder.Services.AddHostedService<VoiceProjectionWorker>();
    builder.Services.AddHostedService<TranscriptionWorker>();
}
if (roles.Has(CxRoleSet.AiOrchestrator)) builder.Services.AddHostedService<AiOrchestratorWorker>();
if (roles.Has(CxRoleSet.AiWorker))
{
    builder.Services.AddHostedService<AiAnalysisWorker>();
    builder.Services.AddHostedService<AiResultPersisterWorker>();
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    // English text must not be escaped into \uXXXX in API responses.
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(
        System.Text.Unicode.UnicodeRanges.All);
});

builder.Services.AddEndpointsApiExplorer();
var mockAuthentication = builder.Configuration["Auth:Mode"]?.Equals(
    "mock", StringComparison.OrdinalIgnoreCase) == true;
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "BestCx API",
        Version = "v1",
        Description = "Customer Experience Data & Intelligence Platform"
    });

    if (!mockAuthentication)
    {
        options.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Name = "X-Api-Key",
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Description = "CX platform API key"
        });

        options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            [new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            }] = Array.Empty<string>()
        });
    }
});

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? Array.Empty<string>();
    if (origins.Length > 0) policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    // No origins configured means same-origin only: the SPA is served by this host.
}));

builder.Services.AddHealthChecks()
    .AddCheck<CxHealthCheck>("cx", tags: new[] { "ready" });

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (ctx, _, ex) =>
        ex is not null ? Serilog.Events.LogEventLevel.Error
        : ctx.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Verbose
        : Serilog.Events.LogEventLevel.Information;
});

app.UseCors();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/v1/portal"))
    {
        // Answers are a few KB; a voice message carries audio, plus the same answers as a form field.
        var maxPortalBytes = context.Request.Path.StartsWithSegments("/v1/portal/voice")
            ? context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<CustomerPortalOptions>>().Value.MaxVoiceBytes + 64 * 1024
            : 32_768;
        if (context.Request.ContentLength > maxPortalBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = "The size of the request exceeds the limit." });
            return;
        }
        var size = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = maxPortalBytes;
    }
    await next(context);
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Liveness never touches a dependency; readiness does.
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    roles = roles.ToString(),
    version = "1.0.0",
    time = DateTimeOffset.UtcNow
})).AllowAnonymous();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Swagger:Enabled", true))
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "BestCx v1");
        options.RoutePrefix = "api-docs";
    });
}

app.MapAuthEndpoints();
if (roles.Has(CxRoleSet.IngestionApi)) app.MapIngestionEndpoints();
if (roles.Has(CxRoleSet.IngestionApi)) app.MapCustomerPortalEndpoints();
if (roles.Has(CxRoleSet.QueryApi))
{
    app.MapCustomerEndpoints();
    app.MapInboxEndpoints();
    app.MapOperationsEndpoints();
    app.MapReportEndpoints();
    app.MapVoiceEndpoints();
    app.MapAnalysisEndpoints();
}
if (roles.Has(CxRoleSet.AdminApi)) app.MapAdminEndpoints();

if (roles.Has(CxRoleSet.Web))
{
    // The CX console ships inside the same image; wwwroot is the built SPA.
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

app.Run();

/// <summary>Exposed so the integration tests can boot the same host with WebApplicationFactory.</summary>
public partial class Program;
