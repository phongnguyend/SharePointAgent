import { useEffect, useRef, useState } from 'react'
import { LoaderCircle, Mic, Square, X } from 'lucide-react'
import { transcribeAudio } from '../api/client'

// Ordered by preference; the server accepts each of these. Safari records only MP4.
const MIME_TYPES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus', 'audio/mp4']

export const dictationSupported = () =>
  typeof window !== 'undefined' && typeof window.MediaRecorder !== 'undefined' && Boolean(navigator.mediaDevices?.getUserMedia)

const clock = (seconds: number) => `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`

function microphoneError(cause: unknown) {
  const name = cause instanceof DOMException ? cause.name : ''
  if (name === 'NotAllowedError' || name === 'SecurityError') {
    return 'Microphone access is blocked. Allow the microphone for this site in your browser settings, then try again.'
  }
  if (name === 'NotFoundError' || name === 'OverconstrainedError') {
    return 'No microphone was found. Connect one and try again.'
  }
  if (name === 'NotReadableError') {
    return 'The microphone is in use by another application.'
  }
  return cause instanceof Error ? cause.message : String(cause)
}

/**
 * Records speech and turns it into text for the composer. Nothing is sent as a message: the text is
 * handed to the caller to review and edit, and the recording is discarded after transcription.
 */
export function DictationButton({ disabled, maxSeconds, onText, onError }: {
  disabled: boolean
  maxSeconds: number
  onText: (text: string) => void
  onError: (message: string | null) => void
}) {
  const [state, setState] = useState<'idle' | 'starting' | 'recording' | 'transcribing'>('idle')
  const [elapsed, setElapsed] = useState(0)
  const recorder = useRef<MediaRecorder | null>(null)
  const stream = useRef<MediaStream | null>(null)
  const chunks = useRef<Blob[]>([])
  const cancelled = useRef(false)
  const timer = useRef<number | undefined>(undefined)
  const recordingStarted = useRef(0)
  const request = useRef<AbortController | null>(null)

  const release = () => {
    window.clearInterval(timer.current)
    stream.current?.getTracks().forEach(track => track.stop())
    stream.current = null
    recorder.current = null
  }

  // Leaving the page must turn the microphone off and drop any pending transcription.
  useEffect(() => () => {
    cancelled.current = true
    if (recorder.current?.state === 'recording') {
      recorder.current.stop()
    }
    release()
    request.current?.abort()
  }, [])

  const stop = (discard: boolean) => {
    cancelled.current = discard
    if (recorder.current?.state === 'recording') {
      recorder.current.stop()
    }
  }

  const start = async () => {
    onError(null)
    setState('starting')
    try {
      const media = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } })
      const mimeType = MIME_TYPES.find(type => MediaRecorder.isTypeSupported(type))
      const instance = new MediaRecorder(media, mimeType ? { mimeType } : undefined)
      stream.current = media
      recorder.current = instance
      chunks.current = []
      cancelled.current = false
      instance.ondataavailable = event => {
        if (event.data.size > 0) {
          chunks.current.push(event.data)
        }
      }
      instance.onstart = () => {
        recordingStarted.current = performance.now()
      }
      instance.onstop = () => {
        // gpt-4o transcription models report tokens but not duration, so the usage report uses this.
        const durationSeconds = recordingStarted.current ? (performance.now() - recordingStarted.current) / 1000 : undefined
        const audio = new Blob(chunks.current, { type: instance.mimeType || mimeType || 'audio/webm' })
        release()
        if (cancelled.current || audio.size === 0) {
          setState('idle')
          return
        }
        setState('transcribing')
        const controller = new AbortController()
        request.current = controller
        transcribeAudio(audio, durationSeconds, controller.signal)
          .then(result => {
            if (result.text.trim()) {
              onText(result.text.trim())
            } else {
              onError('No speech was recognised. Speak closer to the microphone and try again.')
            }
          })
          .catch(cause => {
            if (!controller.signal.aborted) {
              onError(cause instanceof Error ? cause.message : String(cause))
            }
          })
          .finally(() => {
            if (!controller.signal.aborted) {
              setState('idle')
            }
          })
      }
      instance.start(1000)
      setElapsed(0)
      setState('recording')
      const startedAt = Date.now()
      timer.current = window.setInterval(() => {
        const seconds = Math.floor((Date.now() - startedAt) / 1000)
        setElapsed(seconds)
        if (seconds >= maxSeconds) {
          stop(false)
        }
      }, 250)
    } catch (cause) {
      release()
      setState('idle')
      onError(microphoneError(cause))
    }
  }

  if (state === 'recording') {
    return <div className="chat-dictation is-recording" role="group" aria-label="Dictation">
      <button type="button" className="chat-composer-action chat-dictation-stop" onClick={() => stop(false)}
        title="Stop and transcribe">
        <Square size={13} aria-hidden="true" fill="currentColor" />
        <span className="chat-dictation-dot" aria-hidden="true" />
        <span aria-live="off">{clock(elapsed)}</span>
        <span className="visually-hidden">Stop recording and transcribe</span>
      </button>
      <button type="button" className="ghost icon-only chat-dictation-cancel" aria-label="Cancel recording" title="Cancel recording"
        onClick={() => stop(true)}><X size={14} /></button>
    </div>
  }

  return <button type="button" className="chat-composer-action chat-dictation"
    disabled={disabled || state !== 'idle'} onClick={() => void start()}
    title={state === 'transcribing' ? 'Transcribing…' : `Dictate (up to ${Math.round(maxSeconds / 60)} minutes)`}
    aria-label={state === 'transcribing' ? 'Transcribing dictation' : 'Dictate a message'}>
    {state === 'transcribing' || state === 'starting'
      ? <LoaderCircle size={15} className="chat-dictation-spinner" aria-hidden="true" />
      : <Mic size={15} aria-hidden="true" />}
  </button>
}
