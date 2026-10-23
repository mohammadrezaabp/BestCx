import { useEffect, useMemo, useRef, useState } from "react";
import { Link, Navigate, useParams } from "react-router-dom";
import type { InboxKind } from "../feedback";
import { voicePath } from "../workspace";
import { ApiError, api, parseResult } from "../api";
import { Badge, ErrorBox, Loading, PageHead, useAsync } from "../components/Common";
import { useTopics } from "../hooks/useTopics";
import { fmtClock, fmtDateTime, intentLabels, label, resolutionLabels, sentimentLabels, severityLabels, speakerLabels, statusLabels } from "../format";

const speeds = [0.75, 1, 1.25, 1.5, 2];

/**
 * Voice Review workspace (spec 26).
 * Audio plays from a short-lived signed URL; clicking a transcript line or a key moment
 * seeks the player, and the active line follows playback.
 */
export default function VoiceReview({ inbox }: { inbox: InboxKind }) {
  const topics = useTopics();
  const { callId = "" } = useParams();
  const audioRef = useRef<HTMLAudioElement>(null);
  const [position, setPosition] = useState(0);
  const [duration, setDuration] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(1);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const detail = useAsync(() => api.voiceCall(callId), [callId]);
  const transcript = useAsync(
    () =>
      api.transcript(callId).catch((error) => {
        if (error instanceof ApiError && error.status === 404) return null;
        throw error;
      }),
    [callId],
  );
  const audio = useAsync(
    () =>
      api.audioUrl(callId).catch((error) => {
        if (error instanceof ApiError && error.status === 404) return null;
        throw error;
      }),
    [callId],
  );

  const analysis = parseResult(detail.data?.analysis);
  const segments = transcript.data?.segments ?? [];

  const activeIndex = useMemo(() => {
    const ms = position * 1000;
    for (let i = segments.length - 1; i >= 0; i--) {
      if (ms >= segments[i].start_ms) return i;
    }
    return -1;
  }, [position, segments]);

  useEffect(() => {
    if (audioRef.current) audioRef.current.playbackRate = speed;
  }, [speed]);

  function seek(ms: number) {
    const element = audioRef.current;
    if (!element) return;
    element.currentTime = ms / 1000;
    setPosition(ms / 1000);
    void element
      .play()
      .then(() => setPlaying(true))
      .catch(() => undefined);
  }

  function togglePlay() {
    const element = audioRef.current;
    if (!element) return;
    if (element.paused) {
      void element
        .play()
        .then(() => setPlaying(true))
        .catch(() => undefined);
    } else {
      element.pause();
      setPlaying(false);
    }
  }

  async function reprocess(scope: "transcript" | "analysis" | "all") {
    setBusy(true);
    setNotice(null);
    try {
      await api.reprocessCall(callId, {
        scope,
        reason: "reprocess from voice review",
      });
      setNotice(
        "Reprocessing request filed; A new version is created and the previous version is preserved.",
      );
    } catch (err) {
      setNotice(err instanceof Error ? err.message : "Error in registering the request");
    } finally {
      setBusy(false);
    }
  }

  const call = detail.data?.call;
  const durationMs = duration * 1000 || call?.duration_ms || 0;
  if (call && call.inbox !== inbox) return <Navigate to={voicePath(call.inbox, callId)} replace />;

  return (
    <>
      <PageHead
        title={`Contact details ${callId}`}
        subtitle={
          call
            ? `${fmtDateTime(call.started_at)} · ${label(statusLabels, call.direction)} · Agent ${call.agent_id ?? "—"}`
            : undefined
        }
        actions={
          <>
            <Link to={voicePath(inbox)} className="btn">
              Back to calls
            </Link>
            <button
              onClick={() => {
                detail.reload();
                transcript.reload();
                audio.reload();
              }}
            >
              Update
            </button>
            <button onClick={() => reprocess("analysis")} disabled={busy}>
              Reanalysis
            </button>
            <button onClick={() => reprocess("all")} disabled={busy}>
              Transcription + Reanalysis
            </button>
          </>
        }
      />

      <ErrorBox error={detail.error} />
      {notice && (
        <div className="card" style={{ marginBottom: 14 }}>
          {notice}
        </div>
      )}
      {detail.loading && <Loading />}

      {call && (
        <>
          <div className="card" style={{ marginBottom: 14 }}>
            <ErrorBox error={audio.error} />
            {audio.data?.url ? (
              <>
                <audio
                  ref={audioRef}
                  src={audio.data.url}
                  preload="metadata"
                  onTimeUpdate={(e) => setPosition(e.currentTarget.currentTime)}
                  onLoadedMetadata={(e) =>
                    setDuration(e.currentTarget.duration || 0)
                  }
                  onEnded={() => setPlaying(false)}
                  style={{ display: "none" }}
                />

                {/* Progress bar doubles as the seek surface; a real waveform needs
                    peak data the provider does not supply yet. */}
                <div
                  className="waveform"
                  role="slider"
                  aria-label="Sound progress bar"
                  aria-valuenow={Math.round(position)}
                  aria-valuemin={0}
                  aria-valuemax={Math.round(durationMs / 1000)}
                  tabIndex={0}
                  onClick={(e) => {
                    const rect = e.currentTarget.getBoundingClientRect();
                    // LTR layout: measure from the left edge.
                    const ratio = (e.clientX - rect.left) / rect.width;
                    seek(Math.max(0, Math.min(1, ratio)) * durationMs);
                  }}
                  style={{
                    background: `linear-gradient(to right, var(--accent-soft) ${
                      durationMs ? (position * 1000 * 100) / durationMs : 0
                    }%, var(--bg) 0%)`,
                    display: "flex",
                    alignItems: "center",
                    justifyContent: "center",
                    color: "var(--text-dim)",
                    fontSize: 12,
                  }}
                >
                  {segments.length > 0
                    ? `${segments.length} Speech section`
                    : "Click to move"}
                </div>

                <div className="player" style={{ marginTop: 12 }}>
                  <button
                    onClick={() => seek(Math.max(0, position * 1000 - 10000))}
                  >
                    10 seconds ago
                  </button>
                  <button className="primary" onClick={togglePlay}>
                    {playing ? "Pause" : "Play"}
                  </button>
                  <button onClick={() => seek(position * 1000 + 10000)}>
                    10 seconds later
                  </button>
                  <span className="time">
                    {fmtClock(position * 1000)} / {fmtClock(durationMs)}
                  </span>
                  <select
                    value={speed}
                    onChange={(e) => setSpeed(Number(e.target.value))}
                    style={{ width: 90 }}
                  >
                    {speeds.map((s) => (
                      <option key={s} value={s}>
                        {s}×
                      </option>
                    ))}
                  </select>
                </div>
              </>
            ) : (
              <div className="muted">
                The audio file is not yet available.
              </div>
            )}
          </div>

          <div className="voice-layout">
            <div className="card">
              <h3 className="section-title">
                Transcript{" "}
                {transcript.data?.redacted && (
                  <span className="badge warn" style={{ marginInlineStart: 8 }}>
                    Cleaned version
                  </span>
                )}
              </h3>

              {transcript.loading && <Loading />}
              <ErrorBox error={transcript.error} />
              {!transcript.loading && !transcript.data && (
                <div className="muted">
                  The text is not ready yet or you do not have the necessary access.
                </div>
              )}

              {transcript.data && (
                <>
                  <p className="small muted">
                    {transcript.data.engine} / {transcript.data.model} · Confidence
                    Avg{" "}
                    {transcript.data.average_confidence?.toFixed(2) ?? "—"} ·{" "}
                    {fmtDateTime(transcript.data.created_at)}
                  </p>
                  <div className="transcript">
                    {segments.map((segment, index) => (
                      <div
                        key={segment.index}
                        role="button"
                        tabIndex={0}
                        onKeyDown={(event) => {
                          if (event.key === "Enter" || event.key === " ") {
                            event.preventDefault();
                            seek(segment.start_ms);
                          }
                        }}
                        className={`segment ${index === activeIndex ? "active" : ""}`}
                        onClick={() => seek(segment.start_ms)}
                      >
                        <span className="ts">{fmtClock(segment.start_ms)}</span>
                        <span className={`who ${segment.speaker}`}>
                          {label(speakerLabels, segment.speaker)}
                        </span>
                        <span>{segment.text}</span>
                      </div>
                    ))}
                  </div>
                </>
              )}
            </div>

            <div>
              <div className="card" style={{ marginBottom: 14 }}>
                <h3 className="section-title">Analysis of artificial intelligence</h3>

                {!analysis && (
                  <div className="muted">
                    No analysis has been recorded for this call.
                  </div>
                )}

                {analysis && (
                  <>
                    <div
                      style={{
                        display: "flex",
                        gap: 8,
                        flexWrap: "wrap",
                        marginBottom: 10,
                      }}
                    >
                      <Badge prefix="sense" value={analysis.sentiment} map={sentimentLabels} />
                      <Badge prefix="Severity" value={analysis.severity} map={severityLabels} />
                      <Badge
                        prefix="Problem solving"
                        value={analysis.resolution_status}
                        map={resolutionLabels}
                      />
                      <span className="badge">
                        Confidence {analysis.confidence?.toFixed(2)}
                      </span>
                    </div>

                    <dl className="kv">
                      <dt>The reason for the call</dt>
                      <dd>{analysis.call_reason ?? "—"}</dd>
                      <dt>Topic</dt>
                      <dd>{topics.pathLabel(analysis.category, analysis.subcategory)}</dd>
                      <dt>intention</dt>
                      <dd>{label(intentLabels, analysis.intent)}</dd>
                      <dt>Necessary follow-up</dt>
                      <dd>{analysis.follow_up_required ? "Yes" : "No"}</dd>
                      <dt>Model</dt>
                      <dd className="mono small">
                        {detail.data?.analysis?.model_name} ·{" "}
                        {detail.data?.analysis?.prompt_version} ·{" "}
                        {detail.data?.analysis?.taxonomy_version}
                      </dd>
                    </dl>

                    <p style={{ marginTop: 12, lineHeight: 1.9 }}>
                      {analysis.summary}
                    </p>

                    {analysis.action_items?.length > 0 && (
                      <>
                        <h4 className="section-title" style={{ marginTop: 12 }}>
                          Suggested actions
                        </h4>
                        <ul style={{ margin: 0, paddingInlineStart: 18 }}>
                          {analysis.action_items.map((item, i) => (
                            <li key={i}>{item}</li>
                          ))}
                        </ul>
                      </>
                    )}

                    {analysis.key_moments &&
                      analysis.key_moments.length > 0 && (
                        <>
                          <h4
                            className="section-title"
                            style={{ marginTop: 14 }}
                          >
                            Key moments
                          </h4>
                          {analysis.key_moments.map((moment, i) => (
                            <div
                              className="moment"
                              key={i}
                              onClick={() => seek(moment.start_ms)}
                            >
                              <span className="ts mono">
                                {fmtClock(moment.start_ms)}
                              </span>
                              <span className="badge">{moment.type}</span>
                              <span>{moment.summary}</span>
                            </div>
                          ))}
                        </>
                      )}

                  </>
                )}
              </div>

              <div className="card">
                <h3 className="section-title">Audio files</h3>
                {detail.data?.recordings.length === 0 && (
                  <div className="muted">No file has been registered.</div>
                )}
                {detail.data?.recordings.map((recording) => (
                  <dl
                    className="kv"
                    key={recording.id}
                    style={{ marginBottom: 10 }}
                  >
                    <dt>Status</dt>
                    <dd>
                      <span className="badge">{recording.storage_state}</span>
                    </dd>
                    <dt>Duration</dt>
                    <dd className="mono">{fmtClock(recording.duration_ms)}</dd>
                    <dt>checksum</dt>
                    <dd className="mono small">
                      {recording.checksum_sha256?.slice(0, 16) ?? "—"}…
                    </dd>
                    <dt>Confirmed</dt>
                    <dd className="small">
                      {fmtDateTime(recording.verified_at)}
                    </dd>
                  </dl>
                ))}
              </div>
            </div>
          </div>
        </>
      )}
    </>
  );
}
