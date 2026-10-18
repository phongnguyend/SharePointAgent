import { useEffect, useRef, useState } from 'react'
import { PlaygroundRequestTabs, type PlaygroundRequestTab } from '../components/PlaygroundRequestTabs'
import { Mic, Play, RefreshCw, RotateCcw, Square, Upload } from 'lucide-react'
import { getTranscriptionOptions, transcribeAudio, type TranscriptionOptions } from '../api/chatClient'
import { DictationButton, dictationSupported } from '../components/DictationButton'
import { CopyButton, ErrorBanner } from '../components/ui'

const audioTypes: Record<string, string> = { webm: 'audio/webm', ogg: 'audio/ogg', mp4: 'audio/mp4', m4a: 'audio/mp4', mp3: 'audio/mpeg', wav: 'audio/wav' }

export default function SpeechToTextPlaygroundPage({ active }: { active: boolean }) {
  return <PlaygroundRequestTabs label="Speech to text" active={active}>
    {tab => <SpeechToTextRequest tab={tab} />}
  </PlaygroundRequestTabs>
}

function SpeechToTextRequest({ tab }: { tab: PlaygroundRequestTab }) {
  const { active, id, onSummary } = tab
  const [options, setOptions] = useState<TranscriptionOptions | null>(null)
  const [retry, setRetry] = useState(0)
  const [file, setFile] = useState<File | null>(null)
  const [preview, setPreview] = useState('')
  const [duration, setDuration] = useState<number | undefined>()
  const [text, setText] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [running, setRunning] = useState(false)
  const [recordingBusy, setRecordingBusy] = useState(false)
  const request = useRef<AbortController | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const audio = useRef<HTMLAudioElement>(null)
  const busy = running || recordingBusy
  useEffect(() => {
    onSummary(id, '', busy)
  }, [id, onSummary, busy])
  useEffect(() => () => request.current?.abort(), [])
  useEffect(() => {
    if (!active) {
      audio.current?.pause()
      setRecordingBusy(false)
      return
    }
    const controller = new AbortController()
    setError('')
    void getTranscriptionOptions(controller.signal).then(setOptions).catch(cause => {
      if (!controller.signal.aborted) {
        setError(cause instanceof Error ? cause.message : 'Could not load voice configuration.')
      }
    })
    return () => controller.abort()
  }, [active, retry])
  useEffect(() => {
    setDuration(undefined)
    if (!file) {
      setPreview('')
      return
    }
    const url = URL.createObjectURL(file)
    setPreview(url)
    return () => URL.revokeObjectURL(url)
  }, [file])

  const select = (files: FileList | null) => {
    if (busy || !files?.length) {
      return
    }
    const candidate = files[0]
    const type = audioTypes[candidate.name.split('.').at(-1)?.toLowerCase() ?? '']
    if (files.length !== 1 || !type || candidate.size === 0 || candidate.size > (options?.maxBytes ?? 25 * 1024 * 1024)) {
      setError('Select one nonempty WebM, Ogg, MP4, M4A, MP3, or WAV file up to 25 MB.')
      return
    }
    setFile(new File([candidate], candidate.name, { type, lastModified: candidate.lastModified }))
    setText(null)
    setError('')
  }
  const run = async () => {
    if (!file || !options?.enabled || busy || request.current) {
      return
    }
    const controller = new AbortController()
    request.current = controller
    setRunning(true)
    setError('')
    setText(null)
    try {
      const result = await transcribeAudio(file, duration, controller.signal)
      if (!controller.signal.aborted) {
        setText(result.text)
      }
    } catch (cause) {
      setError(controller.signal.aborted ? 'Request cancelled. The provider may already have processed it.'
        : cause instanceof Error ? cause.message : 'Transcription failed.')
    } finally {
      request.current = null
      setRunning(false)
    }
  }

  return <section className="card voice-playground">
    <div className="card-head decision-toolbar"><h2><Mic size={18} />Speech to text</h2><div className="row">
      <button type="button" className="primary" disabled={busy || !file || !options?.enabled} onClick={() => void run()}><Play size={15} />Transcribe</button>
      {running && <button type="button" onClick={() => request.current?.abort()}><Square size={15} />Cancel</button>}
      <button type="button" disabled={busy} onClick={() => { setFile(null); setText(null); setError('') }}><RotateCcw size={15} />Reset</button>
      <button type="button" className="ghost icon-only" disabled={busy} aria-label="Refresh voice configuration" onClick={() => setRetry(value => value + 1)}><RefreshCw size={15} /></button>
    </div></div>
    <div className="card-body stack">
      <p className="decision-description">Transcribe speech using the configured voice model. Audio is not stored; usage is recorded under your user and provider charges may apply.</p>
      <div className="playground-request-boundary">
        {tab.tabBar}
        <div className="stack playground-request-content" role="tabpanel" id={tab.panelId} aria-labelledby={tab.tabId}>
          {error && <ErrorBanner message={error} />}
          {!options ? <p role="status">Loading configuration…</p> : !options.enabled && <p role="status">Voice is not configured. Set AzureOpenAI:TranscriptionDeployment on the server.</p>}
          <div className="decision-playground-grid">
            <div className="decision-panel">
              <div className="decision-panel-heading"><h3>Audio</h3><button type="button" disabled={busy} onClick={() => input.current?.click()}><Upload size={15} />Select file</button></div>
              <input type="file" hidden ref={input} accept=".webm,.ogg,.mp4,.m4a,.mp3,.wav" aria-label="Select audio file" disabled={busy} onChange={event => {
                select(event.target.files)
                event.target.value = ''
              }} />
              <div className="voice-playground-input" onDragOver={event => event.preventDefault()} onDrop={event => {
                event.preventDefault()
                select(event.dataTransfer.files)
              }}>
                <Mic size={32} aria-hidden="true" />
                <p>{file ? file.name : 'Drop an audio file or select one to preview and transcribe.'}</p>
                {preview && <audio ref={audio} controls src={preview} onLoadedMetadata={event => {
                  const seconds = event.currentTarget.duration
                  setDuration(Number.isFinite(seconds) && seconds > 0 ? seconds : undefined)
                }} />}
                <small className="muted">WebM, Ogg, MP4, M4A, MP3, WAV · Maximum 25 MB</small>
                <div className="row">
                  {active && dictationSupported() && <DictationButton disabled={running || !options?.enabled} maxSeconds={options?.maxSeconds ?? 300}
                    onBusyChange={setRecordingBusy} onText={value => { setText(value); setFile(null) }} onError={value => setError(value ?? '')} />}
                  <span>{dictationSupported() ? 'Record with microphone (up to 5 minutes).' : 'Microphone recording is unavailable in this browser. Upload an audio file instead.'}</span>
                </div>
                <small className="muted">Stop recording to transcribe. Switching request tabs or playgrounds cancels microphone recording.</small>
              </div>
            </div>
            <div className="decision-panel" aria-busy={busy}>
              <div className="decision-panel-heading"><h3>Transcript</h3>{text && <CopyButton value={text} />}</div>
              {text !== null ? <pre className="voice-playground-transcript">{text || 'No speech was detected.'}</pre>
                : <div className="decision-response-empty" role="status">{busy ? 'Recording or transcribing…' : 'Record speech or upload audio to begin.'}</div>}
            </div>
          </div>
        </div>
      </div>
    </div>
  </section>
}
