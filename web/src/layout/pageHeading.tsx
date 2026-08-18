import { createContext, useContext, useEffect } from 'react'
import { matchPath } from 'react-router-dom'

export type PageHeading = { title: string, subtitle?: string }

/**
 * Every agent page's title and subtitle live here rather than in the page, because the shell prints
 * them once in the topbar and the page starts at its content. Ordered: the first pattern that
 * matches wins, so a literal route is listed above the parameterised one it would otherwise fall
 * into (`/assets/import` before `/assets/:id`).
 *
 * A page whose title is a record's name — or whose subtitle changes with what is on screen — leaves
 * a sensible resting value here and overrides it through `usePageHeading` once its data has landed.
 */
const headings: (PageHeading & { path: string })[] = [
  { path: '/', title: 'Overview', subtitle: 'Your unified IT environment at a glance.' },

  { path: '/tickets', title: 'Tickets', subtitle: 'Triage, assign, and resolve service work.' },
  { path: '/tickets/:id', title: 'Ticket' },
  { path: '/problems', title: 'Problems', subtitle: 'The causes behind repeated incidents, and their known errors.' },
  { path: '/problems/:id', title: 'Problem' },
  { path: '/knowledge', title: 'Knowledge', subtitle: 'The answers already written down, and who can see them.' },
  { path: '/knowledge/:id', title: 'Article' },

  { path: '/assets/import', title: 'Import configuration items', subtitle: 'Upload a CSV or Excel file, map its columns, and review the dry run.' },
  { path: '/assets/discovery', title: 'Discovery review' },
  { path: '/assets/drift', title: 'Drift', subtitle: 'What the CMDB records, beside what the last scan observed.' },
  { path: '/assets', title: 'Assets', subtitle: 'The configuration items every ticket, alert, and device links back to.' },
  { path: '/assets/:id', title: 'Configuration item' },
  { path: '/scan', title: 'Scan an asset', subtitle: 'Point a phone camera at a label’s QR to open its asset page.' },
  { path: '/topology', title: 'Topology' },

  { path: '/changes', title: 'Changes', subtitle: 'Planned work on the estate, and the maintenance it opens.' },
  { path: '/changes/:id', title: 'Change' },
  { path: '/audits', title: 'Physical audits', subtitle: 'Walk a site with a scanner and confirm what is actually there.' },
  { path: '/audits/:id', title: 'Physical audit' },

  { path: '/software/import', title: 'Import software inventory', subtitle: 'A CSV or Excel export from an agent, an RMM or a collection script.' },
  { path: '/software/licenses', title: 'Licences', subtitle: 'Entitlement blocks per product, and what they cover today.' },
  { path: '/software/products/:id', title: 'Software product' },
  { path: '/software', title: 'Software', subtitle: 'What is installed across the estate, against what was bought.' },

  { path: '/contracts/vendors', title: 'Vendors', subtitle: 'Who the organisation buys from and holds agreements with.' },
  { path: '/contracts', title: 'Contracts', subtitle: 'Support, warranty and lease agreements, and the assets they cover.' },
  { path: '/contracts/:id', title: 'Contract' },
  { path: '/people', title: 'People', subtitle: 'Everyone in the directory, with the assets they hold and the tickets they raised.' },
  { path: '/people/:userId', title: 'Person' },

  { path: '/monitoring', title: 'Monitoring', subtitle: 'Live device status and alerts across the estate.' },
  { path: '/monitoring/alerts', title: 'Monitoring', subtitle: 'Live device status and alerts across the estate.' },
  { path: '/monitoring/devices/:id', title: 'Device' },

  { path: '/admin/users', title: 'Users' },
  { path: '/admin/settings', title: 'Settings', subtitle: 'Configuration that applies across the whole service desk.' },
  { path: '/admin/settings/ticket-categories', title: 'Ticket categories', subtitle: 'What people choose when they raise a ticket.' },
  { path: '/admin/settings/organisation', title: 'Departments and locations', subtitle: 'Where people sit, and the sites the estate is spread across.' },
  { path: '/admin/settings/asset-fields', title: 'Asset fields', subtitle: 'Extra fields each kind of CI carries.' },
  { path: '/admin/settings/sla', title: 'Service levels', subtitle: 'The response and resolution targets a new ticket is held to.' },
]

/** The catch-all route renders the not-found page, so an unmatched path is that and nothing else. */
const unknown: PageHeading = { title: 'Page not found' }

export function headingForPath(pathname: string): PageHeading {
  const match = headings.find((heading) => matchPath(heading.path, pathname) !== null)
  return match ? { title: match.title, subtitle: match.subtitle } : unknown
}

/**
 * The shell hands down the setter, not the value: only the topbar reads a heading, and a page that
 * re-rendered because its own data changed must not re-render every other page in the tree.
 */
export const PageHeadingContext = createContext<(heading: PageHeading | null) => void>(() => {})

/**
 * Overrides the topbar heading for as long as this page is mounted. Pass `null` while the record is
 * still loading and the route's own heading stands in until it arrives.
 */
export function usePageHeading(heading: PageHeading | null) {
  const setHeading = useContext(PageHeadingContext)
  const title = heading?.title
  const subtitle = heading?.subtitle

  useEffect(() => {
    setHeading(title === undefined ? null : { title, subtitle })
    return () => setHeading(null)
  }, [setHeading, title, subtitle])
}
