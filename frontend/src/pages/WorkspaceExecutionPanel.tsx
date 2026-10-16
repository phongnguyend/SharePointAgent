import { useEffect, useRef, useState } from 'react'
import { Copy, Play, Square, Terminal } from 'lucide-react'
import { executePlaygroundScript, type PlaygroundExecutionResult, type WorkspacePlaygroundProvider } from '../api/workspacePlaygroundClient'
import { Modal } from '../components/Modal'
import { ErrorBanner } from '../components/ui'
import { useCopy } from '../lib/useCopy'

export type OpenTerminalRequest = { id: string; instanceId: string; folder: string; scriptPath?: string }
type Props = {
  provider: WorkspacePlaygroundProvider; instanceId: string; enabled: boolean
  request: OpenTerminalRequest; onClose: () => void
}

export const scriptLanguage = (path: string) => {
  const extension = path.split('.').at(-1)?.toLowerCase()
  return ({ py: 'python', js: 'node', mjs: 'node', cjs: 'node', ps1: 'powershell', sh: 'bash', bash: 'bash' } as Record<string, string>)[extension ?? '']
}
const boundedOutput = (text: string) => text.length > 100_000 ? `${text.slice(0, 100_000)}\n[Display truncated at 100,000 characters]` : text

export default function WorkspaceExecutionPanel({ provider, instanceId, enabled, request, onClose }: Props) {
  const copy = useCopy()
  const [language, setLanguage] = useState(request.scriptPath ? scriptLanguage(request.scriptPath) ?? 'bash' : 'bash')
  const [code, setCode] = useState('')
  const [args, setArgs] = useState('[]')
  const [timeout, setTimeoutSeconds] = useState(60)
  const [running, setRunning] = useState(false)
  const [error, setError] = useState('')
  const [result, setResult] = useState<PlaygroundExecutionResult | null>(null)
  const operation = useRef<AbortController | null>(null)
  const canRun = enabled && !running && Number.isInteger(timeout) && timeout >= 1 && timeout <= 220 && (!!request.scriptPath || !!code.trim())

  useEffect(() => () => operation.current?.abort(), [])

  const close = () => {
    operation.current?.abort()
    onClose()
  }

  const execute = async () => {
    if (!canRun || operation.current) {
      return
    }
    let parameters: unknown
    try {
      parameters = JSON.parse(args)
      if (!Array.isArray(parameters) || parameters.some(value => typeof value !== 'string')) {
        throw new Error('Invalid arguments')
      }
    } catch {
      setError('Parameters must be a JSON array of strings, such as ["input.csv", "--verbose"].')
      return
    }
    const controller = new AbortController()
    operation.current = controller
    setRunning(true)
    setError('')
    setResult(null)
    try {
      const response = await executePlaygroundScript(provider, instanceId, {
        language, code: request.scriptPath ? null : code, scriptPath: request.scriptPath ?? null,
        arguments: parameters as string[], workingDirectory: request.folder || '.', timeoutSeconds: timeout,
      }, controller.signal)
      setResult({ ...response, stdout: boundedOutput(response.stdout), stderr: boundedOutput(response.stderr) })
    } catch (cause) {
      setError(controller.signal.aborted ? 'Stopped waiting. The remote command may still finish; refresh files before retrying.'
        : cause instanceof Error ? cause.message : 'Execution failed.')
    } finally {
      operation.current = null
      setRunning(false)
    }
  }

  return <Modal open title={request.scriptPath ? 'Execute script' : 'Terminal'} icon={<Terminal size={18} aria-hidden="true" />}
    className="workspace-execution-dialog" onClose={close}
    footer={<>
      <button type="button" onClick={close}>Close</button>
      {running ? <button type="button" onClick={() => operation.current?.abort()}><Square size={15} aria-hidden="true" />Cancel request</button>
        : <button type="button" className="primary" disabled={!canRun} onClick={() => void execute()}><Play size={15} aria-hidden="true" />{request.scriptPath ? 'Execute script' : 'Run command'}</button>}
    </>}>
    <div className="stack">
      <p className="workspace-instance-id">Instance: {instanceId}</p>
      <p>Working directory: <code>{request.folder || '.'}</code></p>
      {request.scriptPath && <p>Script: <code>{request.scriptPath}</code></p>}
      {error && <ErrorBanner message={error} />}
      <fieldset disabled={!enabled || running} className="workspace-playground-controls stack">
        <div className="row">
          <label>Language<select value={language} onChange={event => setLanguage(event.target.value)}>
            <option value="bash">Bash</option><option value="powershell">PowerShell</option><option value="python">Python</option><option value="node">Node.js</option>
          </select></label>
          <label>Timeout (seconds)<input type="number" min={1} max={220} value={timeout} onChange={event => setTimeoutSeconds(Number(event.target.value))} /></label>
        </div>
        {!request.scriptPath && <label>Command / code<textarea className="workspace-code" autoFocus spellCheck={false} value={code} maxLength={100_000}
          placeholder="Enter a command or script" onChange={event => setCode(event.target.value)} onKeyDown={event => {
            if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
              event.preventDefault()
              void execute()
            }
          }} /></label>}
        <label>Parameters (JSON array)<input autoFocus={!!request.scriptPath} value={args} onChange={event => setArgs(event.target.value)} placeholder={'["input.csv", "--verbose"]'} /></label>
      </fieldset>
      <p className="muted">Each run starts a new process in this directory. Output appears when it finishes. Closing or cancelling stops waiting; remote work may still finish. Refresh files to see changes.</p>
      {running && <p role="status">Running…</p>}
      {result && <div className="stack" role="log" aria-label="Execution output">
        <p>Exit code: <strong>{result.exitCode ?? 'Unavailable'}</strong>{result.timedOut ? ' · Timed out' : ''}</p>
        <h4>Standard output</h4>
        <div className="workspace-output-box">
          <pre className="workspace-output">{result.stdout || '(empty)'}</pre>
          <button type="button" className="ghost icon-only workspace-output-copy" disabled={!result.stdout} onClick={() => void copy(result.stdout, 'Standard output')} aria-label="Copy standard output" title="Copy standard output"><Copy size={14} aria-hidden="true" /></button>
        </div>
        <h4>Standard error</h4>
        <div className="workspace-output-box">
          <pre className="workspace-output">{result.stderr || '(empty)'}</pre>
          <button type="button" className="ghost icon-only workspace-output-copy" disabled={!result.stderr} onClick={() => void copy(result.stderr, 'Standard error')} aria-label="Copy standard error" title="Copy standard error"><Copy size={14} aria-hidden="true" /></button>
        </div>
      </div>}
    </div>
  </Modal>
}
