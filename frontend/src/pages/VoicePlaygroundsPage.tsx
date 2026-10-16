import { useId, useState } from 'react'
import { Mic, Volume2 } from 'lucide-react'
import SpeechToTextPlaygroundPage from './SpeechToTextPlaygroundPage'
import TextToSpeechPlaygroundPage from './TextToSpeechPlaygroundPage'

export default function VoicePlaygroundsPage({ active }: { active: boolean }) {
  const [selected, setSelected] = useState('speech-to-text')
  const id = useId()
  const tabs = [{ key: 'speech-to-text', label: 'Speech to text', Icon: Mic }, { key: 'text-to-speech', label: 'Text to speech', Icon: Volume2 }]
  return <div className="stack">
    <div className="page-tabs page-tabs-secondary" role="tablist" aria-label="Voice playgrounds" onKeyDown={event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
        return
      }
      event.preventDefault()
      const next = event.key === 'Home' ? tabs[0].key : event.key === 'End' ? tabs[1].key : tabs.find(tab => tab.key !== selected)!.key
      setSelected(next)
      event.currentTarget.querySelector<HTMLButtonElement>(`[data-voice-tab="${next}"]`)?.focus()
    }}>
      {tabs.map(({ key, label, Icon }) => <button key={key} type="button" role="tab" id={`${id}-${key}`} data-voice-tab={key}
        aria-controls={`${id}-${key}-panel`} aria-selected={selected === key} tabIndex={selected === key ? 0 : -1} onClick={() => setSelected(key)}><Icon size={15} />{label}</button>)}
    </div>
    {tabs.map(({ key }) => <div key={key} role="tabpanel" id={`${id}-${key}-panel`} aria-labelledby={`${id}-${key}`} hidden={selected !== key}>
      {key === 'speech-to-text' ? <SpeechToTextPlaygroundPage active={active && selected === key} /> : <TextToSpeechPlaygroundPage active={active && selected === key} />}
    </div>)}
  </div>
}
