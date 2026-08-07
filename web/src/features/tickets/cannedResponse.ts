/**
 * Works out what the reply box should contain after inserting a canned response. Cycling through
 * templates replaces the previously inserted one, but anything the agent typed themselves is kept:
 * once the text no longer ends with the last insertion, the new one is appended instead.
 */
export function insertCannedResponse(
  current: string,
  body: string,
  lastInserted: string | null,
): { text: string; inserted: string } {
  const base = lastInserted && current.endsWith(lastInserted)
    ? current.slice(0, current.length - lastInserted.length)
    : current
  const kept = base.trimEnd()
  const inserted = kept ? `\n\n${body}` : body
  return { text: `${kept}${inserted}`, inserted }
}
