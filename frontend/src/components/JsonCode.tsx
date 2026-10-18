import { useMemo, useRef, type ReactNode } from 'react'
import { CopyButton } from './ui'

// Tokenize without parsing so incomplete JSON remains editable. React escapes all text.
function highlight(value: string): ReactNode[] {
  const tokens = /"(?:[^"\\]|\\.)*"|\b(?:true|false|null)\b|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|[{}\[\],:]/g
  const parts: ReactNode[] = []
  let offset = 0
  for (const match of value.matchAll(tokens)) {
    const start = match.index
    parts.push(value.slice(offset, start))
    const token = match[0]
    const kind = token.startsWith('"')
      ? (/^\s*:/.test(value.slice(start + token.length)) ? 'key' : 'string')
      : token === 'null' || token === 'true' || token === 'false' ? 'literal'
        : /^-?\d/.test(token) ? 'number' : 'punctuation'
    parts.push(<span key={start} className={`json-${kind}`}>{token}</span>)
    offset = start + token.length
  }
  parts.push(value.slice(offset))
  return parts
}

export function JsonCode({ value }: { value: string }) {
  const content = useMemo(() => highlight(value), [value])
  return <pre className="decision-json decision-result" tabIndex={0} aria-label="Response JSON"><code>{content}</code></pre>
}

export function JsonCodeBlock({ value }: { value: string }) {
  const content = useMemo(() => highlight(value), [value])
  return <div className="json-code-block">
    <div className="json-code-block-copy"><CopyButton value={value} /></div>
    <pre tabIndex={0} aria-label="Response JSON"><code>{content}</code></pre>
  </div>
}

export function JsonEditor({ id, value, onChange, disabled = false, maxLength = 100000 }: {
  id: string
  value: string
  onChange: (value: string) => void
  disabled?: boolean
  maxLength?: number
}) {
  const backdrop = useRef<HTMLPreElement>(null)
  const content = useMemo(() => highlight(value), [value])
  return <div className="json-editor">
    <pre ref={backdrop} aria-hidden="true" className="json-editor-highlight">{content}{'\n'}</pre>
    <textarea id={id} value={value} maxLength={maxLength} disabled={disabled} wrap="off"
      spellCheck={false} autoCapitalize="off" autoCorrect="off"
      onChange={event => onChange(event.target.value)} onScroll={event => {
        if (backdrop.current) {
          backdrop.current.scrollTop = event.currentTarget.scrollTop
          backdrop.current.scrollLeft = event.currentTarget.scrollLeft
        }
      }} />
  </div>
}
