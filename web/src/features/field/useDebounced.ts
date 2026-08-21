import { useEffect, useState } from 'react'

/**
 * The value, once it has stopped changing for a moment.
 *
 * Search-as-you-type without this is one request per keystroke — on a phone, over field Wi-Fi, that
 * is a dozen requests to type an asset tag and the answers arrive out of order, so the list flickers
 * between results for prefixes the technician has already moved past. The delay is what makes the
 * narrowing read as narrowing rather than as thrashing.
 *
 * 250ms: below about 200 it stops batching a normal typing rhythm, and above about 400 the list
 * feels like it is lagging behind the keyboard.
 */
export function useDebounced<T>(value: T, delayMs = 250): T {
  const [settled, setSettled] = useState(value)

  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), delayMs)
    // Every keystroke cancels the previous timer, so only the last one in a burst ever fires.
    return () => clearTimeout(timer)
  }, [value, delayMs])

  return settled
}
