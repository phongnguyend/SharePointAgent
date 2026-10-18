import { useEffect, useState } from 'react'

export function usePlaygroundMaximized(active = true) {
  const [fullscreen, setFullscreen] = useState(false)
  useEffect(() => {
    if (!fullscreen) {
      return
    }
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !event.defaultPrevented) {
        setFullscreen(false)
      }
    }
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.body.style.overflow = overflow
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [fullscreen])
  useEffect(() => {
    if (!active) {
      setFullscreen(false)
    }
  }, [active])
  const toggleFullscreen = () => setFullscreen(value => !value)
  return { fullscreen, toggleFullscreen }
}
