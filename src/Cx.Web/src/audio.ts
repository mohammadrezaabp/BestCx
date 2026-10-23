/**
 * Browser-side audio preparation for voice feedback.
 *
 * Browsers record WebM/Opus (Chrome) or Ogg (Firefox), and the transcription model accepts
 * WAV, MP3, OGG, FLAC, M4A and AAC. Anything outside that list — including every in-browser
 * recording — is decoded and re-encoded as 16 kHz mono WAV: speech-quality, universally
 * accepted, and small enough (~1.9 MB per minute) to send inline.
 */

const DIRECTLY_ACCEPTED = /^audio\/(wav|x-wav|wave|mpeg|mp3|ogg|flac|x-flac|mp4|m4a|x-m4a|aac)$/i;
const ACCEPTED_EXTENSION = /\.(wav|mp3|ogg|oga|flac|m4a|aac)$/i;
const TARGET_RATE = 16_000;

export interface PreparedAudio {
  blob: Blob;
  durationMs?: number;
}

export async function prepareAudio(file: Blob, name = ""): Promise<PreparedAudio> {
  const accepted =
    DIRECTLY_ACCEPTED.test(file.type) || (!file.type && ACCEPTED_EXTENSION.test(name));
  try {
    const decoded = await decode(file);
    const durationMs = Math.round(decoded.duration * 1000);
    if (accepted) return { blob: file, durationMs };
    return { blob: encodeWav(await downmix(decoded)), durationMs };
  } catch {
    // Formats the browser cannot decode are sent as-is when the server accepts them.
    if (accepted) return { blob: file };
    throw new Error("This audio format cannot be processed. File WAV, MP3 Or M4A Select.");
  }
}

async function decode(file: Blob): Promise<AudioBuffer> {
  const context = new AudioContext();
  try {
    return await context.decodeAudioData(await file.arrayBuffer());
  } finally {
    void context.close();
  }
}

async function downmix(buffer: AudioBuffer): Promise<AudioBuffer> {
  const length = Math.max(1, Math.ceil(buffer.duration * TARGET_RATE));
  const offline = new OfflineAudioContext(1, length, TARGET_RATE);
  const source = offline.createBufferSource();
  source.buffer = buffer;
  source.connect(offline.destination);
  source.start();
  return offline.startRendering();
}

function encodeWav(buffer: AudioBuffer): Blob {
  const samples = buffer.getChannelData(0);
  const view = new DataView(new ArrayBuffer(44 + samples.length * 2));
  const write = (offset: number, text: string) => {
    for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
  };
  write(0, "RIFF");
  view.setUint32(4, 36 + samples.length * 2, true);
  write(8, "WAVE");
  write(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true); // PCM
  view.setUint16(22, 1, true); // mono
  view.setUint32(24, buffer.sampleRate, true);
  view.setUint32(28, buffer.sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  write(36, "data");
  view.setUint32(40, samples.length * 2, true);
  for (let i = 0; i < samples.length; i++) {
    const clamped = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(44 + i * 2, clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff, true);
  }
  return new Blob([view], { type: "audio/wav" });
}
