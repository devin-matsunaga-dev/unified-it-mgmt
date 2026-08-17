import { expect, test } from 'vitest'
import type { KnowledgeDraft } from '../../api/problems'
import { draftAsMarkdown } from './KnowledgeDraftDialog'
import { problemNextStatuses, problemStatusLabel, skipReasonLabel, subjectHref, subjectLabel, windowSummary } from './problemUi'

test('a known error is spelt as two words, and every other status is itself', () => {
  expect(problemStatusLabel('KnownError')).toBe('Known error')
  expect(problemStatusLabel('Investigating')).toBe('Investigating')
})

test('every status can be reopened and none offers itself', () => {
  for (const status of ['Investigating', 'KnownError', 'Resolved', 'Closed'] as const) {
    const next = problemNextStatuses(status)
    expect(next).not.toContain(status)
    expect(next.length).toBeGreaterThan(0)
  }
  expect(problemNextStatuses('Closed')).toEqual(['Investigating'])
})

/**
 * A problem outlives the thing it was about, so a CI whose name came back null says so rather than
 * rendering a bare id or nothing at all.
 */
test('a subject whose CI has been deleted is named as such', () => {
  expect(subjectLabel({ scope: 'Ci', id: 'ci-1', name: null, type: null }))
    .toBe('A configuration item that no longer exists')
  expect(subjectLabel({ scope: 'Ci', id: 'ci-1', name: 'HQ switch', type: 'NetworkDevice' })).toBe('HQ switch')
  expect(subjectLabel(null)).toBe('No configuration item or category')
})

test('a CI subject links to its asset page and a category filters the ticket list', () => {
  expect(subjectHref({ scope: 'Ci', id: 'ci-1', name: 'HQ switch', type: 'NetworkDevice' })).toBe('/assets/ci-1')
  expect(subjectHref({ scope: 'Category', id: 'cat-1', name: 'Network', type: null }))
    .toBe('/tickets?categoryId=cat-1')
})

test('the window is said in whole days, and one incident is not pluralised', () => {
  expect(windowSummary(5, '2026-08-10T00:00:00Z', '2026-08-17T00:00:00Z')).toBe('5 incidents in 7 days')
  expect(windowSummary(1, '2026-08-16T00:00:00Z', '2026-08-17T00:00:00Z')).toBe('1 incident in 1 day')
})

/** A window shorter than a day still reads as a day rather than as zero. */
test('a sub-day window rounds up rather than reading as no time at all', () => {
  expect(windowSummary(3, '2026-08-17T00:00:00Z', '2026-08-17T04:00:00Z')).toBe('3 incidents in 1 day')
})

test('the detector’s vocabulary is said in English, and an unknown reason passes through', () => {
  expect(skipReasonLabel('AlreadyAProblem')).toBe('already a problem')
  expect(skipReasonLabel('SomethingNew')).toBe('SomethingNew')
})

const draft: KnowledgeDraft = {
  problemId: 'prb-1',
  problemNumber: 'PRB-000012',
  title: 'Second floor access point drops clients',
  subjectName: 'HQ floor 2 access point',
  symptoms: [
    { text: 'Wi-Fi keeps dropping', incidentCount: 3 },
    { text: 'Video calls cut out', incidentCount: 1 },
  ],
  rootCause: 'A failing radio.',
  workaround: 'Associate to the floor 3 access point.',
  resolution: 'Replaced under warranty.',
  incidentNumbers: ['INC-000001', 'INC-000002'],
}

test('the draft becomes Markdown with a heading per section', () => {
  const markdown = draftAsMarkdown(draft)

  expect(markdown).toContain('# Second floor access point drops clients')
  expect(markdown).toContain('## Symptoms')
  expect(markdown).toContain('- Wi-Fi keeps dropping (reported 3×)')
  expect(markdown).toContain('## Root cause')
  expect(markdown).toContain('## Workaround')
  expect(markdown).toContain('_Written from PRB-000012._')
})

/** A section nobody filled in is left out of the Markdown rather than emitted as an empty heading. */
test('the Markdown omits sections that were never recorded', () => {
  const markdown = draftAsMarkdown({
    ...draft,
    rootCause: null,
    workaround: null,
    resolution: null,
    symptoms: [],
    incidentNumbers: [],
    subjectName: null,
  })

  expect(markdown).not.toContain('## Root cause')
  expect(markdown).not.toContain('## Symptoms')
  expect(markdown).not.toContain('**About:**')
  expect(markdown).toContain('# Second floor access point drops clients')
})
