/** Mirrors the server's rule: `^[a-zA-Z][a-zA-Z0-9_]*$`, 50 characters at most. */
export const fieldKeyMaxLength = 50

/**
 * The key a label suggests: lower case, words joined by underscores.
 *
 * Deliberately conservative, because the key is permanent — every stored value, import and seeder
 * refers to a field by it, and it cannot be changed after the field is created.
 *
 * Accents are folded rather than dropped ("Café type" gives `cafe_type`, not `caf_type`), and
 * anything the server would reject is removed rather than escaped. A label that yields nothing
 * usable — "3D", "###" — returns an empty string instead of an invented key: the field is left for
 * a person to fill in, which is better than quietly creating something they did not choose.
 */
export function toFieldKey(label: string): string {
  const folded = label
    .normalize('NFD')
    // Combining marks, so an accented letter keeps its base rather than losing the whole character.
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()

  const slug = folded
    .replace(/[^a-z0-9]+/g, '_')
    // The server requires a letter first, so anything before the first one cannot be kept.
    .replace(/^[^a-z]+/, '')
    .replace(/_+/g, '_')
    .replace(/_+$/, '')

  if (slug === '') return ''

  // Truncating can leave a trailing underscore or split a word; neither is invalid, but a key that
  // ends in an underscore reads like a mistake.
  return slug.slice(0, fieldKeyMaxLength).replace(/_+$/, '')
}
