import { useEffect, useRef, useState } from 'react'
import { Download, Play, RefreshCw, RotateCcw, Square, Volume2 } from 'lucide-react'
import { getSpeechOptions, synthesizeSpeech, type SpeechOptions } from '../api/speechPlaygroundClient'
import { ErrorBanner } from '../components/ui'

export default function TextToSpeechPlaygroundPage({ active }: { active: boolean }) {
  const [options, setOptions] = useState<SpeechOptions | null>(null)
  const [retry, setRetry] = useState(0)
  const [text, setText] = useState('')
  const [voice, setVoice] = useState('')
  const [format, setFormat] = useState('mp3')
  const [result, setResult] = useState<{ blob: Blob; format: string } | null>(null)
  const [preview, setPreview] = useState('')
  const [error, setError] = useState('')
  const [running, setRunning] = useState(false)
  const request = useRef<AbortController | null>(null)
  const audio = useRef<HTMLAudioElement>(null)
  useEffect(() => () => request.current?.abort(), [])
  useEffect(() => {
    if (!active) {
      audio.current?.pause()
      return
    }
    const controller = new AbortController()
    setError('')
    void getSpeechOptions(controller.signal).then(value => {
      setOptions(value)
      setVoice(current => value.voices.some(item => item.shortName === current) ? current : value.voices.find(item => item.locale === 'en-US')?.shortName ?? value.voices[0]?.shortName ?? '')
    }).catch(cause => {
      if (!controller.signal.aborted) {
        setError(cause instanceof Error ? cause.message : 'Could not load Speech configuration.')
      }
    })
    return () => controller.abort()
  }, [active, retry])
  useEffect(() => {
    if (!result) {
      setPreview('')
      return
    }
    const url = URL.createObjectURL(result.blob)
    setPreview(url)
    return () => URL.revokeObjectURL(url)
  }, [result])

  const run = async () => {
    if (request.current || !text.trim() || !voice || !options?.configured) {
      return
    }
    const controller = new AbortController()
    request.current = controller
    setRunning(true)
    setError('')
    setResult(null)
    try {
      const blob = await synthesizeSpeech(text, voice, format, controller.signal)
      if (!controller.signal.aborted) {
        setResult({ blob, format })
      }
    } catch (cause) {
      setError(controller.signal.aborted ? 'Request cancelled. Azure may already have processed it.'
        : cause instanceof Error ? cause.message : 'Speech synthesis failed.')
    } finally {
      request.current = null
      setRunning(false)
    }
  }

  return <section className="card speech-playground">
    <div className="card-head decision-toolbar"><h2><Volume2 size={18} />Text to speech</h2><div className="row">
      <button type="button" className="primary" disabled={running || !options?.configured || !voice || !text.trim()} onClick={() => void run()}><Play size={15} />Generate speech</button>
      {running && <button type="button" onClick={() => request.current?.abort()}><Square size={15} />Cancel</button>}
      <button type="button" disabled={running} onClick={() => { setText(''); setResult(null); setError('') }}><RotateCcw size={15} />Reset</button>
      <button type="button" className="ghost icon-only" disabled={running} aria-label="Refresh Speech voices" onClick={() => setRetry(value => value + 1)}><RefreshCw size={15} /></button>
    </div></div>
    <div className="card-body stack">
      <p className="decision-description">Generate speech with Azure AI Speech. Text and audio are not saved by this app. Azure usage charges apply; audio is limited to 10 minutes.</p>
      {error && <ErrorBanner message={error} />}
      {!options && !error && <p role="status">Loading voices…</p>}
      {options && !options.configured && <p role="status">Configure the Speech endpoint and credentials on the server to begin.</p>}
      <div className="speech-playground-settings">
        <label>Voice<select value={voice} disabled={running || !options?.voices.length} onChange={event => setVoice(event.target.value)}>
          {!options?.voices.length && <option value="">No voices available</option>}
          {options?.voices.map(item => <option key={item.shortName} value={item.shortName}>{item.locale} · {item.displayName} · {item.gender} ({item.shortName})</option>)}
        </select></label>
        <label>Output format<select value={format} disabled={running} onChange={event => setFormat(event.target.value)}><option value="mp3">MP3</option><option value="wav">WAV</option></select></label>
      </div>
      <div className="decision-playground-grid">
        <div className="decision-panel speech-playground-editor">
          <div className="decision-panel-heading"><h3>Text</h3><small>{text.length.toLocaleString()} / {(options?.maxCharacters ?? 3000).toLocaleString()}</small></div>
          <textarea aria-label="Text to speak" placeholder="Enter text to turn into speech…" value={text} disabled={running} maxLength={options?.maxCharacters ?? 3000} onChange={event => setText(event.target.value)} />
        </div>
        <div className="decision-panel" aria-busy={running}>
          <div className="decision-panel-heading"><h3>Audio</h3>{preview && result && <a className="button" href={preview} download={`speech.${result.format}`}><Download size={15} />Download {result.format.toUpperCase()}</a>}</div>
          {preview ? <div className="speech-playground-audio"><audio ref={audio} controls src={preview} /></div>
            : <div className="decision-response-empty" role="status">{running ? 'Generating speech…' : 'Generate speech to preview and download the audio.'}</div>}
        </div>
      </div>
    </div>
  </section>
}
