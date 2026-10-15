import { useMemo } from 'react'
import DOMPurify from 'dompurify'
import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import powershell from 'highlight.js/lib/languages/powershell'
import python from 'highlight.js/lib/languages/python'
import javascript from 'highlight.js/lib/languages/javascript'
import typescript from 'highlight.js/lib/languages/typescript'
import markdown from 'highlight.js/lib/languages/markdown'
import json from 'highlight.js/lib/languages/json'
import yaml from 'highlight.js/lib/languages/yaml'
import xml from 'highlight.js/lib/languages/xml'
import css from 'highlight.js/lib/languages/css'
import csharp from 'highlight.js/lib/languages/csharp'
import sql from 'highlight.js/lib/languages/sql'

for (const [name, grammar] of Object.entries({ bash, powershell, python, javascript, typescript, markdown, json, yaml, xml, css, csharp, sql })) {
  hljs.registerLanguage(name, grammar)
}

const languages: Record<string, string> = {
  sh: 'bash', bash: 'bash', zsh: 'bash', ps1: 'powershell', psm1: 'powershell', psd1: 'powershell',
  py: 'python', pyw: 'python', js: 'javascript', jsx: 'javascript', mjs: 'javascript', cjs: 'javascript',
  ts: 'typescript', tsx: 'typescript', mts: 'typescript', cts: 'typescript', md: 'markdown', markdown: 'markdown',
  json: 'json', jsonc: 'json', yaml: 'yaml', yml: 'yaml', xml: 'xml', html: 'xml', svg: 'xml',
  css: 'css', cs: 'csharp', csx: 'csharp', sql: 'sql',
}

/** Display source only: Markdown and HTML are never rendered as active content. */
export default function SourceCode({ path, value }: { path: string; value: string }) {
  const language = languages[path.split('.').pop()?.toLowerCase() ?? '']
  const highlighted = useMemo(() => {
    if (!language) {
      return null
    }
    return DOMPurify.sanitize(hljs.highlight(value, { language, ignoreIllegals: true }).value, {
      ALLOWED_TAGS: ['span'], ALLOWED_ATTR: ['class'],
    })
  }, [language, value])

  return <pre className="source-code" tabIndex={0} aria-label={`${path} source`}>
    {highlighted === null ? <code>{value}</code> : <code className={`language-${language}`} dangerouslySetInnerHTML={{ __html: highlighted }} />}
  </pre>
}
