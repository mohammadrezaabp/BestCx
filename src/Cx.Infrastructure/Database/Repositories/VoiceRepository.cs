using System.Text;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Domain.Entities;
using Dapper;

namespace Cx.Infrastructure.Database.Repositories;

public sealed class VoiceRepository : IVoiceRepository
{
    // A call belongs to exactly one workspace. Sentiment never determines its scope.
    private const string CallInbox = """
        CASE WHEN EXISTS (SELECT 1 FROM feedback scope_feedback
          WHERE scope_feedback.tenant_id=voice_call.tenant_id AND scope_feedback.call_id=voice_call.call_id
            AND scope_feedback.feedback_type='complaint') THEN 'complaints' ELSE 'feedback' END
        """;
    private const string CallColumns = """
        id, tenant_id, call_id, customer_id, agent_id, queue_id, direction, started_at, answered_at,
        ended_at, duration_ms, end_reason, provider, provider_call_id, correlation_id, product_id,
        lifecycle_state, created_at, updated_at
        """;

    private const string RecordingColumns = """
        id, call_id, tenant_id, object_key, content_type, codec, sample_rate, channels, size_bytes,
        duration_ms, checksum_sha256, encryption_key_ref, storage_state, source_uri, language,
        created_at, verified_at
        """;

    private const string TranscriptColumns = """
        id, recording_id, tenant_id, language, engine, model, model_version, status, average_confidence,
        transcript_text, redacted_text, artifact_object_key, redaction_map, supersedes_transcript_id, created_at
        """;

    private readonly CxDataSource _dataSource;

    public VoiceRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task<VoiceCall> UpsertCallAsync(VoiceCall call, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        // Call events arrive out of order (ended can beat started); COALESCE + GREATEST make the
        // upsert order-independent, and lifecycle_state only ever moves forward.
        return await connection.QuerySingleAsync<VoiceCall>(new CommandDefinition($"""
            INSERT INTO voice_call (id, tenant_id, call_id, customer_id, agent_id, queue_id, direction,
                started_at, answered_at, ended_at, duration_ms, end_reason, provider, provider_call_id,
                correlation_id, product_id, lifecycle_state, updated_at)
            VALUES (@Id, @TenantId, @CallId, @CustomerId, @AgentId, @QueueId, @Direction,
                @StartedAt, @AnsweredAt, @EndedAt, @DurationMs, @EndReason, @Provider, @ProviderCallId,
                @CorrelationId, @ProductId, @LifecycleState, now())
            ON CONFLICT (tenant_id, call_id) DO UPDATE
            SET customer_id = COALESCE(EXCLUDED.customer_id, voice_call.customer_id),
                agent_id = COALESCE(EXCLUDED.agent_id, voice_call.agent_id),
                queue_id = COALESCE(EXCLUDED.queue_id, voice_call.queue_id),
                direction = COALESCE(EXCLUDED.direction, voice_call.direction),
                started_at = LEAST(COALESCE(EXCLUDED.started_at, voice_call.started_at),
                                   COALESCE(voice_call.started_at, EXCLUDED.started_at)),
                answered_at = COALESCE(voice_call.answered_at, EXCLUDED.answered_at),
                ended_at = COALESCE(EXCLUDED.ended_at, voice_call.ended_at),
                duration_ms = COALESCE(EXCLUDED.duration_ms, voice_call.duration_ms),
                end_reason = COALESCE(EXCLUDED.end_reason, voice_call.end_reason),
                provider = COALESCE(EXCLUDED.provider, voice_call.provider),
                provider_call_id = COALESCE(EXCLUDED.provider_call_id, voice_call.provider_call_id),
                correlation_id = COALESCE(EXCLUDED.correlation_id, voice_call.correlation_id),
                product_id = COALESCE(EXCLUDED.product_id, voice_call.product_id),
                updated_at = now()
            RETURNING {CallColumns}
            """, new
        {
            Id = call.Id == Guid.Empty ? Guid.NewGuid() : call.Id,
            call.TenantId,
            call.CallId,
            call.CustomerId,
            call.AgentId,
            call.QueueId,
            call.Direction,
            StartedAt = call.StartedAt?.ToUniversalTime(),
            AnsweredAt = call.AnsweredAt?.ToUniversalTime(),
            EndedAt = call.EndedAt?.ToUniversalTime(),
            call.DurationMs,
            call.EndReason,
            call.Provider,
            call.ProviderCallId,
            call.CorrelationId,
            call.ProductId,
            call.LifecycleState
        }, cancellationToken: ct));
    }

    public async Task<VoiceCall?> GetCallAsync(string tenantId, string callId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<VoiceCall>(new CommandDefinition(
            $"SELECT {CallColumns}, {CallInbox} AS inbox FROM voice_call WHERE tenant_id = @tenantId AND call_id = @callId",
            new { tenantId, callId }, cancellationToken: ct));
    }

    public async Task<VoiceCall?> GetCallByIdAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<VoiceCall>(new CommandDefinition(
            $"SELECT {CallColumns}, {CallInbox} AS inbox FROM voice_call WHERE tenant_id = @tenantId AND id = @id",
            new { tenantId, id }, cancellationToken: ct));
    }

    public async Task<Page<VoiceCall>> QueryCallsAsync(VoiceCallQuery query, CancellationToken ct = default)
    {
        var sql = new StringBuilder($"SELECT {CallColumns}, {CallInbox} AS inbox FROM voice_call WHERE tenant_id = @tenantId AND ({CallInbox}) = @inbox");
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", query.TenantId);
        parameters.Add("inbox", query.Inbox);

        if (!string.IsNullOrWhiteSpace(query.CustomerId)) { sql.Append(" AND customer_id = @customerId"); parameters.Add("customerId", query.CustomerId); }
        if (!string.IsNullOrWhiteSpace(query.AgentId)) { sql.Append(" AND agent_id = @agentId"); parameters.Add("agentId", query.AgentId); }
        if (!string.IsNullOrWhiteSpace(query.Direction)) { sql.Append(" AND direction = @direction"); parameters.Add("direction", query.Direction); }
        if (!string.IsNullOrWhiteSpace(query.ProductId)) { sql.Append(" AND product_id = @productId"); parameters.Add("productId", query.ProductId); }
        if (!string.IsNullOrWhiteSpace(query.LifecycleState)) { sql.Append(" AND lifecycle_state = @state"); parameters.Add("state", query.LifecycleState); }
        if (!string.IsNullOrWhiteSpace(query.Search)) { sql.Append(" AND (call_id ILIKE @search OR correlation_id ILIKE @search)"); parameters.Add("search", $"%{query.Search.Trim()}%"); }
        if (query.From is not null) { sql.Append(" AND started_at >= @from"); parameters.Add("from", query.From.Value.ToUniversalTime()); }
        if (query.To is not null) { sql.Append(" AND started_at <= @to"); parameters.Add("to", query.To.Value.ToUniversalTime()); }

        if (query.Cursor is { } c)
        {
            sql.Append(" AND (COALESCE(started_at, created_at), id) < (@cursorTime, @cursorId::uuid)");
            parameters.Add("cursorTime", c.Timestamp);
            parameters.Add("cursorId", c.Tiebreaker);
        }

        var take = Math.Clamp(query.Limit, 1, 200);
        sql.Append(" ORDER BY COALESCE(started_at, created_at) DESC, id DESC LIMIT @limit");
        parameters.Add("limit", take + 1);

        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = (await connection.QueryAsync<VoiceCall>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct))).ToList();

        return Page<VoiceCall>.From(rows, take,
            x => new Cursor(x.StartedAt ?? x.CreatedAt, x.Id.ToString()).Encode());
    }

    public async Task<IReadOnlyList<VoiceCall>> GetRecentForCustomerAsync(string tenantId, string customerId,
        int limit, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<VoiceCall>(new CommandDefinition($"""
            SELECT {CallColumns}, {CallInbox} AS inbox FROM voice_call
            WHERE tenant_id = @tenantId AND customer_id = @customerId
            ORDER BY COALESCE(started_at, created_at) DESC LIMIT @limit
            """, new { tenantId, customerId, limit = Math.Clamp(limit, 1, 100) }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task AdvanceCallStateAsync(Guid callId, string state, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var current = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT lifecycle_state FROM voice_call WHERE id = @callId", new { callId }, cancellationToken: ct));

        if (current is null || !CallLifecycleStates.CanTransition(current, state)) return;

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE voice_call SET lifecycle_state = @state, updated_at = now() WHERE id = @callId",
            new { callId, state }, cancellationToken: ct));
    }

    public async Task<VoiceRecording> UpsertRecordingAsync(VoiceRecording recording, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleAsync<VoiceRecording>(new CommandDefinition($"""
            INSERT INTO voice_recording (id, call_id, tenant_id, object_key, content_type, codec, sample_rate,
                channels, size_bytes, duration_ms, checksum_sha256, encryption_key_ref, storage_state,
                source_uri, language)
            VALUES (@Id, @CallId, @TenantId, @ObjectKey, @ContentType, @Codec, @SampleRate,
                @Channels, @SizeBytes, @DurationMs, @ChecksumSha256, @EncryptionKeyRef, @StorageState,
                @SourceUri, @Language)
            ON CONFLICT (tenant_id, object_key) DO UPDATE
            SET content_type = COALESCE(EXCLUDED.content_type, voice_recording.content_type),
                codec = COALESCE(EXCLUDED.codec, voice_recording.codec),
                sample_rate = COALESCE(EXCLUDED.sample_rate, voice_recording.sample_rate),
                channels = COALESCE(EXCLUDED.channels, voice_recording.channels),
                duration_ms = COALESCE(EXCLUDED.duration_ms, voice_recording.duration_ms),
                source_uri = COALESCE(EXCLUDED.source_uri, voice_recording.source_uri),
                language = COALESCE(EXCLUDED.language, voice_recording.language)
            RETURNING {RecordingColumns}
            """, new
        {
            Id = recording.Id == Guid.Empty ? Guid.NewGuid() : recording.Id,
            recording.CallId,
            recording.TenantId,
            recording.ObjectKey,
            recording.ContentType,
            recording.Codec,
            recording.SampleRate,
            recording.Channels,
            recording.SizeBytes,
            recording.DurationMs,
            recording.ChecksumSha256,
            recording.EncryptionKeyRef,
            recording.StorageState,
            recording.SourceUri,
            recording.Language
        }, cancellationToken: ct));
    }

    public async Task<VoiceRecording?> GetRecordingAsync(Guid recordingId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<VoiceRecording>(new CommandDefinition(
            $"SELECT {RecordingColumns} FROM voice_recording WHERE id = @recordingId",
            new { recordingId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VoiceRecording>> GetRecordingsForCallAsync(Guid callId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<VoiceRecording>(new CommandDefinition(
            $"SELECT {RecordingColumns} FROM voice_recording WHERE call_id = @callId ORDER BY created_at",
            new { callId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task UpdateRecordingStateAsync(Guid recordingId, string state, ObjectWriteResult? stored = null,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var current = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT storage_state FROM voice_recording WHERE id = @recordingId",
            new { recordingId }, cancellationToken: ct));

        if (current is null) return;
        if (!RecordingStorageStates.CanTransition(current, state))
            throw new InvalidOperationException($"illegal recording transition {current} -> {state}");

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE voice_recording
            SET storage_state = @state,
                size_bytes = COALESCE(@sizeBytes, size_bytes),
                checksum_sha256 = COALESCE(@checksum, checksum_sha256),
                verified_at = CASE WHEN @state IN ('VERIFIED','READY') THEN COALESCE(verified_at, now()) ELSE verified_at END
            WHERE id = @recordingId
            """, new
        {
            recordingId,
            state,
            sizeBytes = stored?.SizeBytes,
            checksum = stored?.ChecksumSha256
        }, cancellationToken: ct));
    }

    public async Task<Guid> InsertTranscriptAsync(VoiceTranscript transcript,
        IReadOnlyList<VoiceTranscriptSegment> segments, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var id = transcript.Id == Guid.Empty ? Guid.NewGuid() : transcript.Id;

        // A prior transcript is never overwritten: the new row points back at it (spec 18).
        var previous = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
            SELECT id FROM voice_transcript
            WHERE recording_id = @recordingId AND status = 'SUCCEEDED'
            ORDER BY created_at DESC LIMIT 1
            """, new { recordingId = transcript.RecordingId }, tx, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO voice_transcript (id, recording_id, tenant_id, language, engine, model, model_version,
                status, average_confidence, transcript_text, redacted_text, artifact_object_key, redaction_map,
                supersedes_transcript_id)
            VALUES (@Id, @RecordingId, @TenantId, @Language, @Engine, @Model, @ModelVersion,
                @Status, @AverageConfidence, @TranscriptText, @RedactedText, @ArtifactObjectKey,
                @RedactionMap::jsonb, @SupersedesTranscriptId)
            """, new
        {
            Id = id,
            transcript.RecordingId,
            transcript.TenantId,
            transcript.Language,
            transcript.Engine,
            transcript.Model,
            transcript.ModelVersion,
            transcript.Status,
            transcript.AverageConfidence,
            transcript.TranscriptText,
            transcript.RedactedText,
            transcript.ArtifactObjectKey,
            transcript.RedactionMap,
            SupersedesTranscriptId = transcript.SupersedesTranscriptId ?? previous
        }, tx, cancellationToken: ct));

        foreach (var segment in segments)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO voice_transcript_segment (transcript_id, segment_index, speaker, speaker_ref,
                    start_ms, end_ms, text, redacted_text, confidence)
                VALUES (@TranscriptId, @SegmentIndex, @Speaker, @SpeakerRef, @StartMs, @EndMs, @Text,
                    @RedactedText, @Confidence)
                ON CONFLICT (transcript_id, segment_index) DO NOTHING
                """, new
            {
                TranscriptId = id,
                segment.SegmentIndex,
                segment.Speaker,
                segment.SpeakerRef,
                segment.StartMs,
                segment.EndMs,
                segment.Text,
                segment.RedactedText,
                segment.Confidence
            }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<VoiceTranscript?> GetLatestTranscriptAsync(Guid recordingId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<VoiceTranscript>(new CommandDefinition($"""
            SELECT {TranscriptColumns} FROM voice_transcript
            WHERE recording_id = @recordingId AND status = 'SUCCEEDED'
            ORDER BY created_at DESC LIMIT 1
            """, new { recordingId }, cancellationToken: ct));
    }

    public async Task<VoiceTranscript?> GetTranscriptAsync(Guid transcriptId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<VoiceTranscript>(new CommandDefinition(
            $"SELECT {TranscriptColumns} FROM voice_transcript WHERE id = @transcriptId",
            new { transcriptId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VoiceTranscriptSegment>> GetSegmentsAsync(Guid transcriptId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<VoiceTranscriptSegment>(new CommandDefinition("""
            SELECT id, transcript_id, segment_index, speaker, speaker_ref, start_ms, end_ms, text,
                   redacted_text, confidence
            FROM voice_transcript_segment
            WHERE transcript_id = @transcriptId
            ORDER BY segment_index
            """, new { transcriptId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task UpdateSegmentTextAsync(Guid transcriptId, int segmentIndex, string text,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE voice_transcript_segment SET text = @text
            WHERE transcript_id = @transcriptId AND segment_index = @segmentIndex
            """, new { transcriptId, segmentIndex, text }, cancellationToken: ct));
    }
}
