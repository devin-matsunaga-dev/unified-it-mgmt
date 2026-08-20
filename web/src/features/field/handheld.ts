import { useEffect, useState } from 'react'

/**
 * Below this the agent shell is unusable: DESIGN.md §10 sets its floor at 1280px, and everything
 * narrower is a phone held in one hand. 768px rather than something closer to the floor because a
 * tablet in landscape genuinely can drive the desktop screens, and being sent to the field surface
 * on one would feel like being demoted.
 */
const handheldQuery = '(max-width: 768px)'

/** Reactive because a phone rotating is a resize, not a reload. */
export function useIsHandheld(): boolean {
  const [isHandheld, setIsHandheld] = useState(() => window.matchMedia?.(handheldQuery).matches ?? false)

  useEffect(() => {
    const media = window.matchMedia?.(handheldQuery)
    if (!media) return
    const update = (event: MediaQueryListEvent) => setIsHandheld(event.matches)
    media.addEventListener('change', update)
    setIsHandheld(media.matches)
    return () => media.removeEventListener('change', update)
  }, [])

  return isHandheld
}
