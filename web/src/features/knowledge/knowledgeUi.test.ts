import { expect, test } from 'vitest'
import { kbStatusTone, kbTransitionLabel, parseArticleBody } from './knowledgeUi'

/**
 * The body parser (WP-5.9). It exists because articles are written as Markdown and this app carries no
 * Markdown library — and because whatever renders an article body must produce React nodes and never HTML.
 */
test('headings, bullets and numbered steps each become their own block', () => {
  const blocks = parseArticleBody([
    '## Before you start',
    '',
    'You need your phone.',
    '',
    '1. Open the client.',
    '2. Press connect.',
    '',
    '- It may ask twice.',
    '- That is normal.',
  ].join('\n'))

  expect(blocks).toEqual([
    { kind: 'heading', level: 2, text: 'Before you start' },
    { kind: 'paragraph', text: 'You need your phone.' },
    { kind: 'list', ordered: true, items: ['Open the client.', 'Press connect.'] },
    { kind: 'list', ordered: false, items: ['It may ask twice.', 'That is normal.'] },
  ])
})

/** Consecutive lines are one paragraph, which is how prose is written and how the seeded articles wrap. */
test('wrapped lines join into one paragraph and wrapped list items stay with their bullet', () => {
  const blocks = parseArticleBody([
    'The network drops',
    'for about a minute.',
    '',
    '- Move to another desk if you can;',
    '  the drop-outs stop immediately.',
  ].join('\n'))

  expect(blocks).toEqual([
    { kind: 'paragraph', text: 'The network drops for about a minute.' },
    { kind: 'list', ordered: false, items: ['Move to another desk if you can; the drop-outs stop immediately.'] },
  ])
})

/**
 * The safety property, stated as a test: nothing in a body is markup. Anything the parser does not
 * recognise falls through as a paragraph, and every block becomes a React text node.
 */
test('html in an article body is text, not a block', () => {
  expect(parseArticleBody('<script>alert(1)</script>')).toEqual([
    { kind: 'paragraph', text: '<script>alert(1)</script>' },
  ])
})

test('an empty body is no blocks rather than one empty one', () => {
  expect(parseArticleBody('')).toEqual([])
  expect(parseArticleBody('\n\n   \n')).toEqual([])
})

/** Published is the only state that means anybody outside the service desk can read it, so it is the green one. */
test('the status tones separate published from everything else', () => {
  expect(kbStatusTone('Published')).toContain('green')
  expect(kbStatusTone('Draft')).toContain('amber')
  expect(kbStatusTone('Archived')).toContain('slate')
})

/** The buttons say the verb, not the state they land in. */
test('transition labels read as actions', () => {
  expect(kbTransitionLabel('Published')).toBe('Publish')
  expect(kbTransitionLabel('Draft')).toBe('Back to draft')
  expect(kbTransitionLabel('Archived')).toBe('Archive')
})
