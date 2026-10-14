import { Empty } from './ui'

export default function CapabilitySelector({ kind, items, selected, disabled, onChange }: {
  kind: 'tools' | 'skills'
  items: { name: string; description: string }[]
  selected: string[] | null
  disabled: boolean
  onChange: (names: string[]) => void
}) {
  const names = selected ?? items.map(item => item.name)
  return <fieldset className="sub-agent-form stack agent-capability-selector" disabled={disabled} aria-label={`${kind} selection`}>
    <div className="sub-agent-selection-toolbar">
      <span className="hint">{names.length} selected · {items.length} available</span>
      <div className="row">
        <button type="button" disabled={!items.length || (selected !== null && items.every(item => names.includes(item.name)))}
          onClick={() => onChange([...new Set([...names, ...items.map(item => item.name)])])}>Select all</button>
        <button type="button" disabled={selected !== null && names.length === 0} onClick={() => onChange([])}>Unselect all</button>
      </div>
    </div>
    <p className="hint">{selected === null ? 'Uses all available capabilities, including future additions. Changing the selection saves an explicit list.' : 'Only selected capabilities are offered. No selections means none.'} {kind === 'skills' ? 'Skills include their scripts and resources.' : 'Tools also require their services and workspace mode to be available.'}</p>
    <div className="sub-agent-options">
      {[...items, ...names.filter(name => !items.some(item => item.name === name)).map(name => ({ name, description: 'Not available in this deployment. Deselect to remove.' }))].map(item => <label className="sub-agent-option" key={item.name}>
        <input type="checkbox" checked={names.includes(item.name)} onChange={() => onChange(names.includes(item.name) ? names.filter(name => name !== item.name) : [...names, item.name])} />
        <span><strong>{item.name}</strong><small>{item.description}</small></span>
      </label>)}
      {!items.length && !names.length ? <Empty title={`No ${kind} available`} /> : null}
    </div>
  </fieldset>
}
