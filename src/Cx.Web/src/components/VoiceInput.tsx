import { useEffect, useRef, useState } from "react";
import { prepareAudio, type PreparedAudio } from "../audio";
import { fmtBytes, fmtClock } from "../format";
import Icon from "./Icon";

const MAX_RECORD_MS = 10 * 60 * 1000;
const MAX_UPLOAD_BYTES = 18 * 1024 * 1024;

/** Record the customer's message in the browser, or choose an audio file. */
export default function VoiceInput({
  value,
  onChange,
  disabled,
}: {
  value: PreparedAudio | null;
  onChange: (audio: PreparedAudio | null) => void;
  disabled?: boolean;
}) {
  const [recording, setRecording] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  const [preparing, setPreparing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<string | null>(null);
  const recorder = useRef<MediaRecorder | null>(null);
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => {
    if (!value) return setPreview(null);
    const url = URL.createObjectURL(value.blob);
    setPreview(url);
    return () => URL.revokeObjectURL(url);
  }, [value]);

  useEffect(
    () => () => {
      window.clearInterval(timer.current);
      recorder.current?.stream.getTracks().forEach((track) => track.stop());
    },
    [],
  );

  async function accept(blob: Blob, name = "") {
    setPreparing(true);
    setError(null);
    try {
      const prepared = await prepareAudio(blob, name);
      if (prepared.blob.size > MAX_UPLOAD_BYTES)
        throw new Error("The audio file is larger than 18 MB; Choose a shorter file.");
      onChange(prepared);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      onChange(null);
    } finally {
      setPreparing(false);
    }
  }

  async function start() {
    setError(null);
    if (!navigator.mediaDevices?.getUserMedia) {
      setError("Your browser does not support audio recording (Need to HTTPS Or localhost).");
      return;
    }
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const chunks: BlobPart[] = [];
      const media = new MediaRecorder(stream);
      media.ondataavailable = (event) => event.data.size && chunks.push(event.data);
      media.onstop = () => {
        stream.getTracks().forEach((track) => track.stop());
        window.clearInterval(timer.current);
        setRecording(false);
        void accept(new Blob(chunks, { type: media.mimeType || "audio/webm" }));
      };
      recorder.current = media;
      media.start(1000);
      const began = Date.now();
      setElapsed(0);
      setRecording(true);
      onChange(null);
      timer.current = window.setInterval(() => {
        const ms = Date.now() - began;
        setElapsed(ms);
        if (ms >= MAX_RECORD_MS) media.stop();
      }, 250);
    } catch {
      setError("Access to the microphone was not granted. Check the browser permission.");
    }
  }

  if (recording)
    return (
      <div className="voice-input recording" role="status">
        <span className="rec-dot" aria-hidden="true" />
        <strong>Recording</strong>
        <span className="mono">{fmtClock(elapsed)}</span>
        <button className="primary" type="button" onClick={() => recorder.current?.stop()}>
          <Icon name="stop" size={16} /> End of recording
        </button>
      </div>
    );

  return (
    <div className="voice-input">
      {error && (
        <div className="error" role="alert">
          {error}
        </div>
      )}
      {preparing && <div className="muted small">Preparing the sound…</div>}
      {value && preview ? (
        <div className="voice-preview">
          <audio controls src={preview} />
          <span className="small muted">
            {value.durationMs ? fmtClock(value.durationMs) : ""} · {fmtBytes(value.blob.size)}
          </span>
          <button type="button" onClick={() => onChange(null)} disabled={disabled}>
            <Icon name="close" size={15} /> Delete
          </button>
        </div>
      ) : (
        <div className="voice-choices">
          <button
            type="button"
            className="voice-choice"
            onClick={start}
            disabled={disabled || preparing}
          >
            <Icon name="voice" size={26} />
            <strong>Audio recording</strong>
          </button>
          <label className={`voice-choice ${disabled || preparing ? "disabled" : ""}`}>
            <Icon name="upload" size={26} />
            <strong>Audio file selection</strong>
            <input
              type="file"
              accept="audio/*,.wav,.mp3,.ogg,.m4a,.webm,.flac,.aac"
              disabled={disabled || preparing}
              onChange={(event) => {
                const file = event.target.files?.[0];
                event.target.value = "";
                if (file) void accept(file, file.name);
              }}
            />
          </label>
        </div>
      )}
    </div>
  );
}
