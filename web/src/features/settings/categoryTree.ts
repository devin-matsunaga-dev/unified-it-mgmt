import type { TicketCategory } from '../../api/helpdesk'

/** Mirrors CategoryService.MaximumDepth. The server is the authority; this only stops a doomed request. */
export const maximumDepth = 3

export type FlatCategory = {
  category: TicketCategory
  depth: number
  /** Ancestors first, e.g. "Hardware › Laptops". Used for the parent picker and for delete prompts. */
  path: string
}

/** Depth-first in the order the server sorted them, so the table reads top to bottom like the tree. */
export function flatten(categories: TicketCategory[], depth = 0, prefix = ''): FlatCategory[] {
  return categories.flatMap((category) => {
    const path = prefix ? `${prefix} › ${category.name}` : category.name
    return [{ category, depth, path }, ...flatten(category.children, depth + 1, path)]
  })
}

/** A category and everything under it — the set an edit may not be re-parented into. */
export function subtreeIds(category: TicketCategory): string[] {
  return [category.id, ...category.children.flatMap(subtreeIds)]
}

/**
 * Parents a category may legally be given. Excludes itself and its own descendants (a cycle), and
 * anything already deep enough that a child would breach the depth limit.
 *
 * `editing` is null when creating, in which case only the depth rule applies.
 */
export function parentOptions(categories: TicketCategory[], editing: TicketCategory | null): FlatCategory[] {
  const blocked = new Set(editing ? subtreeIds(editing) : [])
  return flatten(categories).filter((item) => !blocked.has(item.category.id) && item.depth + 1 < maximumDepth)
}

/** How many categories exist in total, including nested ones. */
export function totalCount(categories: TicketCategory[]): number {
  return categories.reduce((total, category) => total + 1 + totalCount(category.children), 0)
}
