import {
  ciLifecycleLabel, ciLifecycleStates,
} from './lifecycle'
import { ciTypeLabel, ciTypes, type CiCustomField, type CiFilter, type CiLifecycleState, type CiType } from '../../api/assets'
import { FilterCombobox, type ComboboxOption } from '../../components/ui/FilterCombobox'

/**
 * What each filter control needs to draw itself. Passed in rather than reached for, so this file
 * stays a description of the filter bar and does no data fetching of its own.
 */
export type CiFilterContext = {
  filter: CiFilter
  setFilter: (update: (current: CiFilter) => CiFilter) => void
  ownerOptions: ComboboxOption[]
  /** The "choose one" fields of the selected type; empty until a type is chosen. */
  subFilters: CiCustomField[]
}

export type CiFilterId = 'type' | 'lifecycleState' | 'owner' | 'isActive'

export type CiFilterDefinition = {
  id: CiFilterId
  /** What the chooser calls it. */
  label: string
  render: (context: CiFilterContext) => React.ReactNode
}

/**
 * The filter bar, defined once so it can be chosen from.
 *
 * Search is deliberately absent: it is the primary way into this list rather than a narrowing of it,
 * and a table whose search box somebody has hidden is one they cannot get back to without finding a
 * menu. The sub-filters are part of `type` for the same reason they only appear once a type is
 * chosen — they belong to it, and offering them separately would mean a chooser entry that does
 * nothing most of the time.
 */
export const ciFilterDefinitions: readonly CiFilterDefinition[] = [
  {
    id: 'type',
    label: 'Type',
    render: ({ filter, setFilter, subFilters }) => <>
      <select aria-label="Filter by type" className="input w-auto min-w-40" value={filter.type ?? ''}
        onChange={(event) => setFilter((current) => ({
          ...current,
          type: (event.target.value || undefined) as CiType | undefined,
          // The sub-filters belong to the type being left, so they go with it. Keeping them would
          // silently narrow the new type by a field it does not have and return nothing.
          customFields: undefined,
          page: 1,
        }))}>
        <option value="">All types</option>
        {ciTypes.map((type) => <option key={type} value={type}>{ciTypeLabel(type)}</option>)}
      </select>

      {subFilters.map((field) => <select key={field.id}
        aria-label={`Filter by ${field.label}`}
        className="input w-auto min-w-40"
        value={filter.customFields?.find((item) => item.fieldId === field.id)?.value ?? ''}
        onChange={(event) => setFilter((current) => {
          const rest = (current.customFields ?? []).filter((item) => item.fieldId !== field.id)
          const chosen = event.target.value
          const next = chosen ? [...rest, { fieldId: field.id, value: chosen }] : rest
          return { ...current, customFields: next.length > 0 ? next : undefined, page: 1 }
        })}>
        <option value="">All {field.label.toLowerCase()}</option>
        {field.options.map((option) => <option key={option} value={option}>{option}</option>)}
      </select>)}
    </>,
  },
  {
    id: 'lifecycleState',
    label: 'Lifecycle state',
    render: ({ filter, setFilter }) => <select aria-label="Filter by lifecycle state"
      className="input w-auto min-w-40" value={filter.lifecycleState ?? ''}
      onChange={(event) => setFilter((current) => ({
        ...current,
        lifecycleState: (event.target.value || undefined) as CiLifecycleState | undefined,
        page: 1,
      }))}>
      <option value="">All lifecycle states</option>
      {ciLifecycleStates.map((state) => <option key={state} value={state}>{ciLifecycleLabel(state)}</option>)}
    </select>,
  },
  {
    id: 'owner',
    label: 'Owner',
    // A combobox rather than a select: this is the one filter whose list grows with the organisation,
    // and a native select cannot be typed into.
    render: ({ filter, setFilter, ownerOptions }) => <FilterCombobox
      label="Filter by owner"
      className="w-56"
      emptyLabel="All owners"
      value={filter.ownerUserId ?? null}
      options={ownerOptions}
      onChange={(ownerUserId) => setFilter((current) => ({ ...current, ownerUserId: ownerUserId ?? undefined, page: 1 }))} />,
  },
  {
    id: 'isActive',
    label: 'Active state',
    render: ({ filter, setFilter }) => <select aria-label="Filter by state" className="input w-auto min-w-36"
      value={filter.isActive === undefined ? '' : String(filter.isActive)}
      onChange={(event) => setFilter((current) => ({
        ...current,
        isActive: event.target.value === '' ? undefined : event.target.value === 'true',
        page: 1,
      }))}>
      <option value="">Active and inactive</option>
      <option value="true">Active only</option>
      <option value="false">Inactive only</option>
    </select>,
  },
]

export const ciFilterIds = ciFilterDefinitions.map((definition) => definition.id)

export function ciFilterDefinition(id: CiFilterId): CiFilterDefinition {
  const found = ciFilterDefinitions.find((definition) => definition.id === id)
  if (!found) throw new Error(`Unknown asset filter '${id}'.`)
  return found
}

/**
 * The part of a filter a hidden control owns, cleared when it is hidden.
 *
 * A filter still narrowing the list from behind a control nobody can see is the worst outcome here:
 * the table would show a subset with no visible reason, and the way to widen it would be to find the
 * chooser and turn the control back on.
 */
export function clearFilter(id: CiFilterId, filter: CiFilter): CiFilter {
  switch (id) {
    // Hiding the type control takes its sub-filters with it — they cannot be reached without it.
    case 'type': return { ...filter, type: undefined, customFields: undefined, page: 1 }
    case 'lifecycleState': return { ...filter, lifecycleState: undefined, page: 1 }
    case 'owner': return { ...filter, ownerUserId: undefined, page: 1 }
    case 'isActive': return { ...filter, isActive: undefined, page: 1 }
  }
}
