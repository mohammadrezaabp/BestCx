using Cx.Application.Abstractions;
using Cx.Application.Ai;
using Cx.Application.Common;
using Cx.Application.Ingestion;
using Cx.Application.Observability;
using Cx.Application.Options;
using Cx.Application.Privacy;
using Cx.Application.Query;
using Cx.Application.Operations;
using Cx.Infrastructure.Ai;
using Cx.Infrastructure.Database;
using Cx.Infrastructure.Database.Repositories;
using Cx.Infrastructure.Messaging;
using Cx.Infrastructure.Security;
using Cx.Infrastructure.Operations;
using Cx.Infrastructure.Storage;
using Cx.Infrastructure.Stt;
using Cx.Contracts.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Polly.CircuitBreaker;

namespace Cx.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCxInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<IngestionOptions>(configuration.GetSection(IngestionOptions.Section));
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.Section));
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.Section));
        services.Configure<MockDataOptions>(configuration.GetSection(MockDataOptions.Section));
        services.Configure<ObjectStorageOptions>(configuration.GetSection(ObjectStorageOptions.Section));
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.Section));
        services.Configure<VoiceOptions>(configuration.GetSection(VoiceOptions.Section));
        services.Configure<RedactionOptions>(configuration.GetSection(RedactionOptions.Section));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));

        services.TryAddSingleton(TimeProvider.System);

        AddData(services);
        AddMessaging(services, configuration);
        AddStorage(services);
        AddAi(services, configuration);
        AddStt(services, configuration);

        services.AddSingleton<IEventValidator, EventValidator>();
        services.AddSingleton<IEventNormalizer, EventNormalizer>();
        services.AddSingleton<IRedactionService, RedactionService>();
        services.AddSingleton<IAiResultValidator, AiResultValidator>();
        services.AddSingleton<IPromptBuilder>(sp =>
            new PromptBuilder(sp.GetRequiredService<IOptions<AiOptions>>().Value.PromptVersion));
        services.AddScoped<IContextBuilder, ContextBuilder>();
        services.AddScoped<IDecisionMaker, DecisionMaker>();
        services.AddScoped<IFollowUpTaskWriter, FollowUpTaskWriter>();
        services.AddScoped<ICaseRouter, CaseRouter>();
        services.AddScoped<IEventIngestionService, EventIngestionService>();

        return services;
    }

    private static void AddData(IServiceCollection services)
    {
        services.AddSingleton<CxDataSource>();
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<MockDataSeeder>();

        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<CustomerPortalAccounts>();
        services.AddScoped<UserAccounts>();
        services.AddScoped<OperationsStore>();
        services.AddScoped<IFeedbackRepository, FeedbackRepository>();
        services.AddScoped<ISupportCaseRepository, SupportCaseRepository>();
        services.AddScoped<IVoiceRepository, VoiceRepository>();
        services.AddScoped<IAiAnalysisRepository, AiAnalysisRepository>();
        services.AddScoped<IHumanReviewRepository, HumanReviewRepository>();
        services.AddScoped<ITaxonomyRepository, TaxonomyRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IProcessingJobRepository, ProcessingJobRepository>();
        services.AddScoped<IIntegrationCheckpointRepository, IntegrationCheckpointRepository>();
        services.AddScoped<IInsightsRepository, InsightsRepository>();
        services.AddScoped<IReportRepository, ReportRepository>();
    }

    private static void AddMessaging(IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Messaging:Provider"] ?? "kafka";

        if (provider.Equals("inmemory", StringComparison.OrdinalIgnoreCase))
        {
            // Single-process mode for tests and local experiments; no broker required.
            services.AddSingleton<InMemoryBus>();
            services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<InMemoryBus>());
            services.AddSingleton<IMessageSubscriber>(sp => sp.GetRequiredService<InMemoryBus>());
        }
        else
        {
            services.AddSingleton<IMessagePublisher, KafkaPublisher>();
            services.AddSingleton<IMessageSubscriber, KafkaSubscriber>();
            services.AddSingleton<KafkaTopicProvisioner>();
        }
    }

    private static void AddStorage(IServiceCollection services) =>
        services.AddSingleton<IObjectStorage, S3ObjectStorage>();

    private static void AddAi(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();

        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
        {
            // Without a key the platform still runs end to end on the deterministic stub,
            // whose output always lands in human review.
            services.AddSingleton<IAiCompletionClient, StubCompletionClient>();
            services.AddSingleton<IAiDecisionClient, DisabledDecisionClient>();
            return;
        }

        services.AddHttpClient<IAiCompletionClient, OpenRouterCompletionClient>()
            .AddPolicyHandler(AiResiliencePolicy(options));
        services.AddHttpClient<IAiDecisionClient, OpenRouterDecisionClient>()
            .AddPolicyHandler(AiResiliencePolicy(options));
    }

    /// <summary>Retry with exponential backoff + jitter, then a circuit breaker (spec 31).</summary>
    private static IAsyncPolicy<HttpResponseMessage> AiResiliencePolicy(AiOptions options)
    {
        var retry = Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .OrResult(r => (int)r.StatusCode >= 500 || (int)r.StatusCode == 429)
            .WaitAndRetryAsync(
                Math.Max(0, options.MaxAttempts - 1),
                attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)) +
                           TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)));

        var breaker = Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .OrResult(r => (int)r.StatusCode >= 500)
            .AdvancedCircuitBreakerAsync(
                failureThreshold: 0.5,
                samplingDuration: TimeSpan.FromSeconds(60),
                minimumThroughput: 8,
                durationOfBreak: TimeSpan.FromSeconds(30));

        return Policy.WrapAsync(retry, breaker);
    }

    private static void AddStt(IServiceCollection services, IConfiguration configuration)
    {
        var voice = configuration.GetSection(VoiceOptions.Section).Get<VoiceOptions>() ?? new VoiceOptions();
        var ai = configuration.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
        var openRouterKey = string.IsNullOrWhiteSpace(voice.Stt.ApiKey) ? ai.ApiKey : voice.Stt.ApiKey;

        if (voice.Stt.Engine.Equals("openrouter", StringComparison.OrdinalIgnoreCase) &&
            ai.Enabled && !string.IsNullOrWhiteSpace(openRouterKey))
        {
            // Same key and gateway as analysis: real English transcription with no extra vendor.
            services.AddHttpClient<ISpeechToTextEngine, OpenRouterSpeechToTextEngine>()
                .AddPolicyHandler(AiResiliencePolicy(ai));
        }
        else if (voice.Stt.Engine.Equals("deepgram", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(voice.Stt.DeepgramApiKey))
        {
            // No retry policy: the audio is streamed, and a consumed stream cannot be resent.
            // A failed attempt is retried by the transcription job instead.
            services.AddHttpClient<ISpeechToTextEngine, DeepgramSpeechToTextEngine>();
        }
        else if (voice.Stt.Engine.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(voice.Stt.ApiKey))
        {
            services.AddHttpClient<ISpeechToTextEngine, OpenAiCompatibleSpeechToTextEngine>();
        }
        else
        {
            // No key: placeholder text only. The UI labels stub transcripts so they are never
            // mistaken for what the customer actually said.
            services.AddSingleton<ISpeechToTextEngine, StubSpeechToTextEngine>();
        }
    }

    /// <summary>Authentication + the permission policies every protected endpoint names.</summary>
    public static IServiceCollection AddCxAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        var jwtMode = auth.Mode.Equals("jwt", StringComparison.OrdinalIgnoreCase);
        var mockMode = auth.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase);
        var loginMode = auth.Mode.Equals("login", StringComparison.OrdinalIgnoreCase);
        var scheme = jwtMode ? CxAuthSchemes.Jwt
            : mockMode ? CxAuthSchemes.Mock
            : loginMode ? CxAuthSchemes.Session
            : CxAuthSchemes.ApiKey;

        var builder = services.AddAuthentication(scheme);
        builder.AddCookie(CustomerPortalAccounts.Scheme, CustomerPortalAccounts.ConfigureCookie);

        builder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            CxAuthSchemes.ApiKey, _ => { });

        if (mockMode)
        {
            builder.AddScheme<AuthenticationSchemeOptions, MockAuthenticationHandler>(
                CxAuthSchemes.Mock, _ => { });
        }

        if (loginMode)
            builder.AddCookie(CxAuthSchemes.Session, options => SessionCookie.Configure(options, auth));

        if (jwtMode)
        {
            builder.AddJwtBearer(CxAuthSchemes.Jwt, options =>
            {
                options.Authority = auth.Authority;
                options.Audience = auth.Audience;
                options.RequireHttpsMetadata = auth.RequireHttpsMetadata;
                options.TokenValidationParameters.RoleClaimType = auth.RolesClaim;
            });
        }

        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            PermissionAuthorizationHandler>();

        // Policies may only name schemes that are actually registered: listing an absent
        // handler makes every authorized request throw at runtime.
        var schemes = jwtMode ? new[] { CxAuthSchemes.ApiKey, CxAuthSchemes.Jwt }
            : loginMode ? new[] { CxAuthSchemes.Session, CxAuthSchemes.ApiKey }
            : new[] { scheme };

        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(schemes)
                .RequireAuthenticatedUser()
                .Build());

        services.AddAuthorization(options =>
        {
            options.AddPolicy(CustomerPortalAccounts.Policy, policy => policy
                .AddAuthenticationSchemes(CustomerPortalAccounts.Scheme)
                .RequireAuthenticatedUser());
            foreach (var permission in CxPermissions.All)
            {
                options.AddPolicy(permission, policy => policy
                    .AddAuthenticationSchemes(schemes)
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(permission)));
            }
        });

        return services;
    }

    /// <summary>OpenTelemetry traces and metrics for every service in the image (spec 30).</summary>
    public static IServiceCollection AddCxTelemetry(this IServiceCollection services, IConfiguration configuration,
        string serviceName)
    {
        var otlpEndpoint = configuration["Otel:Endpoint"]
                           ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceVersion: "1.0.0")
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = configuration["Ingestion:Environment"] ?? "development"
            });

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resource)
                    .AddSource(CxMetrics.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(o =>
                        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resource)
                    .AddMeter(CxMetrics.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            });

        return services;
    }
}
