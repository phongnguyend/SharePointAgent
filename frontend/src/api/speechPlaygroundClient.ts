import { API_BASE, authorizedFetch } from '../auth'
import { request, toError } from './http'

export type SpeechVoice = { shortName: string; displayName: string; locale: string; gender: string }
export type SpeechOptions = { configured: boolean; maxCharacters: number; voices: SpeechVoice[] }
export const getSpeechOptions = (signal?: AbortSignal) => request<SpeechOptions>('/api/admin/speech-playground/options', { signal })
export async function synthesizeSpeech(text: string, voice: string, format: string, signal: AbortSignal): Promise<Blob> {
  const response = await authorizedFetch(`${API_BASE}/api/admin/speech-playground`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text, voice, format }), signal,
  })
  if (!response.ok) {
    throw await toError(response)
  }
  return response.blob()
}
