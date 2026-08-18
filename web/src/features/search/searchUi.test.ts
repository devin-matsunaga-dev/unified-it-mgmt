import { describe, expect, it } from 'vitest'
import type { SearchGroup, SearchHit, SearchResults, SearchResultType } from '../../api/search'
import {
  describeTruncation,
  flattenHits,
  hitKey,
  moveHighlight,
  searchGroupLabel,
  searchResultHref,
  visibleGroups,
} from './searchUi'

function hit(type: SearchResultType, id: string): SearchHit {
  return { type, id, title: `${type} ${id}`, reference: null, subtitle: null, badge: null }
}

function group(
  type: SearchResultType,
  hits: SearchHit[],
  overrides: Partial<SearchGroup> = {},
): SearchGroup {
  return {
    type,
    status: 'Searched',
    returned: hits.length,
    total: hits.length,
    truncated: false,
    hits,
    ...overrides,
  }
}

function results(groups: SearchGroup[]): SearchResults {
  const returnedCount = groups.reduce((sum, item) => sum + item.returned, 0)
  const totalCount = groups.reduce((sum, item) => sum + item.total, 0)
  return {
    term: 'core',
    limit: 5,
    types: ['Ticket', 'Ci', 'Device', 'Alert', 'User'],
    summary: { returnedCount, totalCount, truncated: totalCount > returnedCount },
    groups,
  }
}

describe('searchResultHref', () => {
  it('sends each kind to the page that already owns it', () => {
    expect(searchResultHref(hit('Ticket', 't1'))).toBe('/tickets/t1')
    expect(searchResultHref(hit('Ci', 'c1'))).toBe('/assets/c1')
    expect(searchResultHref(hit('Device', 'd1'))).toBe('/monitoring/devices/d1')
    expect(searchResultHref(hit('User', 'u1'))).toBe('/people/u1')
    // WP-5.9's kind, and the agent route: this bar lives in the agent shell, and the portal has a reader
    // of its own at /portal/kb/:id.
    expect(searchResultHref(hit('KbArticle', 'k1'))).toBe('/knowledge/k1')
  })

  /** The group heading is what somebody scans for, so it is the word the app uses everywhere else. */
  it('labels the knowledge group', () => {
    expect(searchGroupLabel('KbArticle')).toBe('Knowledge')
  })

  /**
   * An alert has no page of its own — it is a drawer on the board, opened by a query parameter. The same
   * deep link WP-3.10's notifications and WP-5.3's timeline use, so an alert opens identically wherever it
   * was found.
   */
  it('opens an alert as the board drawer rather than inventing a page for it', () => {
    expect(searchResultHref(hit('Alert', 'a1'))).toBe('/monitoring/alerts?alertId=a1')
  })
})

describe('visibleGroups', () => {
  /**
   * The distinction the whole response shape exists for. A group this user may not read is dropped, not
   * drawn empty: "Assets — nothing found" is a claim about the estate, and the truth is a claim about their
   * account.
   */
  it('drops a group the caller may not read rather than rendering it empty', () => {
    const data = results([
      group('Ticket', [hit('Ticket', 't1')]),
      group('Ci', [], { status: 'NotPermitted' }),
      group('Device', [], { status: 'NotPermitted' }),
    ])

    expect(visibleGroups(data).map((item) => item.type)).toEqual(['Ticket'])
  })

  it('drops a searched group that genuinely found nothing, so no empty heading is drawn', () => {
    const data = results([group('Ticket', [hit('Ticket', 't1')]), group('Ci', [])])

    expect(visibleGroups(data).map((item) => item.type)).toEqual(['Ticket'])
  })

  it('keeps the server order, which is the order the groups are meant to be read in', () => {
    const data = results([
      group('Ticket', [hit('Ticket', 't1')]),
      group('Ci', [hit('Ci', 'c1')]),
      group('Alert', [hit('Alert', 'a1')]),
    ])

    expect(visibleGroups(data).map((item) => item.type)).toEqual(['Ticket', 'Ci', 'Alert'])
  })
})

describe('flattenHits', () => {
  /**
   * One flat list across the headings. This is what the arrow keys walk, and it is the same list the screen
   * draws — the two coming from different functions is how Down opens the wrong record.
   */
  it('runs across the group headings so Down at the end of one group reaches the next', () => {
    const data = results([
      group('Ticket', [hit('Ticket', 't1'), hit('Ticket', 't2')]),
      group('Ci', [], { status: 'NotPermitted' }),
      group('Alert', [hit('Alert', 'a1')]),
    ])

    expect(flattenHits(data).map(hitKey)).toEqual(['Ticket-t1', 'Ticket-t2', 'Alert-a1'])
  })
})

describe('moveHighlight', () => {
  it('starts at the first result going down and the last going up', () => {
    expect(moveHighlight(-1, 1, 3)).toBe(0)
    expect(moveHighlight(-1, -1, 3)).toBe(2)
  })

  /**
   * Wrapping rather than stopping at the ends: the list is short, and a key that silently does nothing
   * reads as a broken control.
   */
  it('wraps at both ends', () => {
    expect(moveHighlight(2, 1, 3)).toBe(0)
    expect(moveHighlight(0, -1, 3)).toBe(2)
  })

  it('highlights nothing when there is nothing to highlight', () => {
    expect(moveHighlight(-1, 1, 0)).toBe(-1)
    expect(moveHighlight(0, 1, 0)).toBe(-1)
  })
})

describe('describeTruncation', () => {
  it('says nothing when nothing was left out', () => {
    expect(describeTruncation(results([group('Ticket', [hit('Ticket', 't1')])]))).toBeNull()
  })

  /**
   * WP-2.4's rule at the bottom of the dropdown: a capped list states the number it did not show, per kind,
   * so five of ninety reads as ninety rather than as everything there is.
   */
  it('names each capped kind and its real total', () => {
    const data = results([
      group('Ticket', [hit('Ticket', 't1')], { returned: 1, total: 42, truncated: true }),
      group('Ci', [hit('Ci', 'c1')], { returned: 1, total: 7, truncated: true }),
      group('Alert', [hit('Alert', 'a1')]),
    ])

    expect(describeTruncation(data)).toBe('42 tickets, 7 assets in all — keep typing to narrow it')
  })
})

describe('searchGroupLabel', () => {
  /**
   * The headings are the names the sidebar uses, not the server's type names — an operator looking for a
   * laptop scans for "Assets" rather than for "Ci".
   */
  it('uses the words the navigation already uses', () => {
    expect(searchGroupLabel('Ci')).toBe('Assets')
    expect(searchGroupLabel('User')).toBe('People')
    expect(searchGroupLabel('Ticket')).toBe('Tickets')
  })
})
