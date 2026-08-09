import type { CiRelationship, CiRelationshipType, CiType } from '../../api/assets'

export const ciRelationshipTypes: CiRelationshipType[] = ['RunsOn', 'ConnectsTo', 'DependsOn', 'HostedOn']

/**
 * Each type as the verb that makes an edge a sentence. WP-2.3 fixed source → target to read "source
 * needs target", so these only ever run in that direction.
 */
const relationshipVerbs: Record<CiRelationshipType, string> = {
  RunsOn: 'runs on',
  ConnectsTo: 'connects to',
  DependsOn: 'depends on',
  HostedOn: 'is hosted on',
}

export function ciRelationshipVerb(type: string) {
  return relationshipVerbs[type as CiRelationshipType] ?? type
}

/** Which way an edge points from the CI whose page is open: upstream is what it needs. */
export type RelationshipDirection = 'Upstream' | 'Downstream'

/** The far end of an edge, so a row can name the other CI without the reader working out which end it is. */
export type RelationshipCounterpart = {
  direction: RelationshipDirection
  ciId: string
  name: string
  type: CiType
}

export function relationshipCounterpart(edge: CiRelationship, ciId: string): RelationshipCounterpart {
  return edge.sourceCiId === ciId
    ? { direction: 'Upstream', ciId: edge.targetCiId, name: edge.targetCiName, type: edge.targetCiType }
    : { direction: 'Downstream', ciId: edge.sourceCiId, name: edge.sourceCiName, type: edge.sourceCiType }
}

/** One edge as plain English, always read source-first so the arrow and the words agree. */
export function describeRelationship(edge: CiRelationship): string {
  return `${edge.sourceCiName} ${ciRelationshipVerb(edge.type)} ${edge.targetCiName}`
}
