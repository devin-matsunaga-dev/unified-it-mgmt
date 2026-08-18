import { expect, test } from 'vitest'
import type { TicketCategory } from '../../api/helpdesk'
import { flatten, parentOptions, subtreeIds, totalCount } from './categoryTree'

function category(id: string, name: string, children: TicketCategory[] = []): TicketCategory {
  return { id, name, parentId: null, isActive: true, sortOrder: 0, fields: [], children }
}

/** Hardware › Laptops › Screens, plus a flat Software root. */
const tree: TicketCategory[] = [
  category('hardware', 'Hardware', [
    category('laptops', 'Laptops', [category('screens', 'Screens')]),
    category('printers', 'Printers'),
  ]),
  category('software', 'Software'),
]

test('flatten reads depth-first so the table renders in tree order', () => {
  expect(flatten(tree).map((row) => row.category.id))
    .toEqual(['hardware', 'laptops', 'screens', 'printers', 'software'])
  expect(flatten(tree).map((row) => row.depth)).toEqual([0, 1, 2, 1, 0])
})

test('flatten builds an ancestor path for each row', () => {
  const paths = Object.fromEntries(flatten(tree).map((row) => [row.category.id, row.path]))
  expect(paths.screens).toBe('Hardware › Laptops › Screens')
  expect(paths.software).toBe('Software')
})

test('subtreeIds returns the category and everything beneath it', () => {
  expect(subtreeIds(tree[0])).toEqual(['hardware', 'laptops', 'screens', 'printers'])
})

/** A third-level category has no room beneath it, so offering it as a parent would only earn a 400. */
test('parentOptions omits categories that are already at the depth limit', () => {
  expect(parentOptions(tree, null).map((row) => row.category.id))
    .toEqual(['hardware', 'laptops', 'printers', 'software'])
})

/** Re-parenting a category into its own descendant is the cycle the server rejects; do not offer it. */
test('parentOptions omits the category being edited and its own descendants', () => {
  const laptops = tree[0].children[0]

  expect(parentOptions(tree, laptops).map((row) => row.category.id)).toEqual(['hardware', 'printers', 'software'])
})

test('totalCount counts nested categories, not just roots', () => {
  expect(totalCount(tree)).toBe(5)
})
