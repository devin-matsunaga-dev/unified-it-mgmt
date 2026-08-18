import type { CiType } from '../../api/assets'

/**
 * The progressive views (§1). Each one is a server-side CI type filter, because that is the only cut
 * the topology API takes — and cutting on the server keeps the node limit spent on what the view is
 * actually about rather than on things it will then discard.
 *
 * Endpoint folding is orthogonal and stays on in every view: a view decides what is drawn, folding
 * decides how many nodes that takes.
 */
export type TopologyView = {
  id: 'overview' | 'network' | 'infrastructure' | 'applications' | 'everything'
  label: string
  /** Null means every type. */
  types: CiType[] | null
  /** Shown under the title, so the view says what it is rather than making somebody click to find out. */
  description: string
}

export const topologyViews: readonly TopologyView[] = [
  {
    id: 'overview',
    label: 'Overview',
    // Every type except Hardware. "Important" is not a fact this CMDB records, and picking important
    // servers by connection count or by name would be inference — so the honest cut is the one §1
    // actually names: not every workstation and laptop.
    types: ['NetworkDevice', 'Server', 'Virtual', 'Software', 'Logical'],
    description: 'The estate without the desk equipment — network, servers and the services on them.',
  },
  {
    id: 'network',
    label: 'Network',
    types: ['NetworkDevice'],
    description: 'Routers, firewalls and switches, drawn edge-first by the role each one records.',
  },
  {
    id: 'infrastructure',
    label: 'Infrastructure',
    // Network devices stay: an infrastructure map that cannot show which switch a hypervisor hangs
    // off answers half the question it was opened for.
    types: ['NetworkDevice', 'Server', 'Virtual'],
    description: 'Hypervisors, servers and VMs, with the network they depend on.',
  },
  {
    id: 'applications',
    label: 'Applications',
    // Servers and VMs stay for the same reason: "where does this run" is the point of the view.
    types: ['Software', 'Logical', 'Server', 'Virtual'],
    description: 'Applications and services, down to the machines they run on.',
  },
  {
    id: 'everything',
    label: 'Everything',
    types: null,
    description: 'Every CI with a relationship, including endpoints. The exploratory view.',
  },
]

/** Overview first: it is the view that stays readable as the estate grows. */
export const defaultView = topologyViews[0]

export function viewById(id: string): TopologyView {
  return topologyViews.find((view) => view.id === id) ?? defaultView
}
