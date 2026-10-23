using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Cx.Application.Observability;

/// <summary>
/// Required metrics from spec 30. Labels are deliberately low cardinality:
/// customer_id, event_id and call_id are NEVER used as metric tags.
/// </summary>
public static class CxMetrics
{
    public const string MeterName = "Cx.Platform";
    public const string ActivitySourceName = "Cx.Platform";

    public static readonly ActivitySource Activity = new(ActivitySourceName, "1.0.0");
    private static readonly Meter Meter = new(MeterName, "1.0.0");

    // ingestion
    public static readonly Counter<long> IngestTotal =
        Meter.CreateCounter<long>("cx_ingest_total", "events", "Events accepted by the ingestion API");
    public static readonly Counter<long> IngestInvalidTotal =
        Meter.CreateCounter<long>("cx_ingest_invalid_total", "events", "Events rejected to the invalid stream");
    public static readonly Histogram<double> IngestLatency =
        Meter.CreateHistogram<double>("cx_ingest_latency", "ms", "Ingestion latency to Kafka ack");

    // persistence
    public static readonly Counter<long> PersistenceTotal =
        Meter.CreateCounter<long>("cx_persistence_total", "events", "Events persisted to PostgreSQL");
    public static readonly Counter<long> PersistenceFailures =
        Meter.CreateCounter<long>("cx_persistence_failures", "events", "Persistence failures");
    public static readonly Counter<long> DuplicateTotal =
        Meter.CreateCounter<long>("cx_duplicate_total", "events", "Duplicate event_id deliveries absorbed");
    public static readonly Histogram<double> PersistenceLatency =
        Meter.CreateHistogram<double>("cx_persistence_latency", "ms", "Consume-to-commit latency");

    // outbox / bus
    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("cx_outbox_published_total", "events", "Outbox rows published");

    // ai
    public static readonly Counter<long> AiJobsTotal =
        Meter.CreateCounter<long>("cx_ai_jobs_total", "jobs", "AI analysis jobs started");
    public static readonly Counter<long> AiFailuresTotal =
        Meter.CreateCounter<long>("cx_ai_failures_total", "jobs", "AI analysis failures");
    public static readonly Histogram<double> AiLatency =
        Meter.CreateHistogram<double>("cx_ai_latency", "ms", "AI provider round-trip latency");
    public static readonly Counter<long> AiOutputInvalidTotal =
        Meter.CreateCounter<long>("cx_ai_output_invalid_total", "responses", "Model outputs that failed schema validation");

    // stt
    public static readonly Counter<long> SttJobsTotal =
        Meter.CreateCounter<long>("cx_stt_jobs_total", "jobs", "Transcription jobs started");
    public static readonly Counter<long> SttFailuresTotal =
        Meter.CreateCounter<long>("cx_stt_failures_total", "jobs", "Transcription failures");
    public static readonly Histogram<double> SttLatency =
        Meter.CreateHistogram<double>("cx_stt_latency", "ms", "Transcription latency");

    // voice storage
    public static readonly Counter<long> RecordingsStored =
        Meter.CreateCounter<long>("cx_voice_recordings_stored_total", "recordings", "Recordings verified and stored");
    public static readonly Counter<long> RecordingFailures =
        Meter.CreateCounter<long>("cx_voice_recording_failures_total", "recordings", "Recording ingest failures");

    // dlq
    public static readonly Counter<long> DlqTotal =
        Meter.CreateCounter<long>("cx_dlq_total", "messages", "Messages routed to a dead letter topic");

    /// <summary>Gauges are observed from the database so restarts do not lose the value.</summary>
    public static void RegisterGauge(string name, string unit, string description, Func<long> observe) =>
        Meter.CreateObservableGauge(name, observe, unit, description);

    public static void RegisterGauge(string name, string unit, string description,
        Func<IEnumerable<Measurement<long>>> observe) =>
        Meter.CreateObservableGauge(name, observe, unit, description);
}
