import { useCallback } from 'react'
import { useToast } from '../components/Toast'
import { copyText } from './clipboard'

export function useCopy() {
  const toast = useToast()
  return useCallback(async (value: string, label = 'Text') => {
    const copied = await copyText(value)
    if (copied) {
      toast.success(`${label} copied to clipboard.`)
    } else {
      toast.error('Could not copy to clipboard. Select the text and copy it manually.')
    }
    return copied
  }, [toast])
}
