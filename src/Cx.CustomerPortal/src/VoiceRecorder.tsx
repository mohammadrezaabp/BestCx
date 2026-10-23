import { useEffect, useRef, useState } from "react";
import { prepareAudio, type PreparedAudio } from "./audio";

export const MAX_RECORD_MS = 5 * 60 * 1000;
// The server accepts 10 MB; 5 minutes of 16 kHz mono WAV is about 9.6 MB.
const MAX_UPLOAD_BYTES = 10 * 1024 * 1024;

function clock(ms: number) {
  const seconds = Math.floor(ms / 1000);
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

/** Record the customer's message in the browser and hand back speech-quality WAV. */
export default function VoiceRecorder({
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
  const [error, setError] = useState("");
  const [preview, setPreview] = useState<string | null>(null);
  const recorder = useRef<MediaRecorder | null>(null);
  const timer = useRef<number>();

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

  async function accept(blob: Blob) {
    setPreparing(true);
    try {
      const prepared = await prepareAudio(blob);
      if (prepared.blob.size > MAX_UPLOAD_BYTES)
        throw new Error("The voice message is too long; Record shorter.");
      onChange(prepared);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      onChange(null);
    } finally {
      setPreparing(false);
    }
  }

  async function start() {
    setError("");
    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === "undefined") {
      setError("Your browser does not support audio recording; Please write your experience.");
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

  return (
    <div className="recorder">
      {recording ? (
        <div className="recorder-live" role="status">
          <span className="rec-dot" aria-hidden="true" />
          <strong>Recording</strong>
          <span className="recorder-clock" dir="ltr">
            {clock(elapsed)} / {clock(MAX_RECORD_MS)}
          </span>
          <button
            type="button"
            className="recorder-stop"
            onClick={() => recorder.current?.stop()}
          >
            ■ End of recording
          </button>
        </div>
      ) : preparing ? (
        <div className="recorder-note" role="status">
          Preparing the sound…
        </div>
      ) : value && preview ? (
        <div className="recorder-preview">
          <audio controls src={preview} />
          <div>
            {value.durationMs ? (
              <span className="recorder-clock" dir="ltr">
                {clock(value.durationMs)}
              </span>
            ) : null}
            <button
              type="button"
              className="recorder-again"
              onClick={() => {
                onChange(null);
                void start();
              }}
              disabled={disabled}
            >
              Record again
            </button>
          </div>
        </div>
      ) : (
        <button
          type="button"
          className="recorder-start"
          onClick={start}
          disabled={disabled}
        >
          <span className="mic" aria-hidden="true">
            ●
          </span>
          <span>
            <strong>Start recording</strong>
            <small>Maximum 5 minutes; You can hear it when it’s over.</small>
          </span>
        </button>
      )}
      {error && (
        <div className="error" role="alert">
          {error}
        </div>
      )}
    </div>
  );
}
