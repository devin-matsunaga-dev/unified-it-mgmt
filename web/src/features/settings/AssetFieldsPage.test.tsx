import { QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { assetsApi, type CiCustomField, type CiTypeSchema } from '../../api/assets'
import { ApiError } from '../../api/client'
import { AssetFieldsPage } from './AssetFieldsPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return {
    ...actual,
    assetsApi: {
      ...actual.assetsApi,
      listTypeSchemas: vi.fn(),
      createCustomField: vi.fn(),
      updateCustomField: vi.fn(),
      deleteCustomField: vi.fn(),
      getCustomFieldValueCounts: vi.fn(),
    },
  }
})

const toasts = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }))
vi.mock('sonner', () => ({ toast: toasts }))

const hardwareType: CiCustomField = {
  id: 'field-kind', ciType: 'Hardware', key: 'hardware_type', label: 'Hardware type',
  type: 'Select', isRequired: false, options: ['Laptop', 'Desktop', 'Printer'], sortOrder: 0,
}

function schemas(customFields: CiCustomField[] = [hardwareType]): CiTypeSchema[] {
  return [
    { type: 'Hardware', attributes: [], customFields },
    { type: 'Server', attributes: [], customFields: [] },
  ]
}

function renderPage() {
  const client = new QueryClient({
    // An explicit cache handler, because a query that is expected to fail otherwise leaves vitest
    // reporting its rejection as unhandled even though the component renders the error state.
    queryCache: new QueryCache({ onError: () => {} }),
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><AssetFieldsPage /></MemoryRouter>
    </QueryClientProvider>)
}

// A block body, not a concise arrow: returning the mock would hand vitest a function, which it
// treats as a teardown callback and calls after the test — invoking the API mock with nobody to
// catch it, which surfaced as an unhandled rejection in the failure-path test below.
beforeEach(() => {
  vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue(schemas())
  vi.mocked(assetsApi.getCustomFieldValueCounts).mockResolvedValue([])
})
afterEach(() => vi.clearAllMocks())

test('lists the fields of the chosen CI type with their options', async () => {
  renderPage()

  const row = (await screen.findByText('Hardware type')).closest('tr')!
  expect(within(row).getByText('Laptop')).toBeInTheDocument()
  expect(within(row).getByText('Printer')).toBeInTheDocument()
  expect(within(row).getByText('Choose one')).toBeInTheDocument()
})

test('a failed load offers a retry rather than looking empty', async () => {
  vi.mocked(assetsApi.listTypeSchemas).mockRejectedValue(new ApiError(500, 'nope'))

  renderPage()

  expect(await screen.findByText('Fields could not be loaded')).toBeInTheDocument()
  expect(screen.queryByText(/No extra fields/)).not.toBeInTheDocument()
})

/** Fields belong to one CI type, so the tabs must actually change what is listed. */
test('switching CI type shows that type instead', async () => {
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('tab', { name: 'Server' }))

  expect(screen.queryByText('Hardware type')).not.toBeInTheDocument()
  expect(screen.getByText(/No extra fields on Server/)).toBeInTheDocument()
})

/** This is the whole ask: adding the hardware kinds an organisation actually uses. */
test('a choose-one field is created with the options entered', async () => {
  vi.mocked(assetsApi.createCustomField).mockResolvedValue(hardwareType)
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'New field' }))
  await user.type(screen.getByLabelText('Label'), 'Form factor')
  // The key is generated from the label; this test leaves it as generated.
  await user.type(screen.getByLabelText('New option'), 'Tower')
  await user.click(screen.getByRole('button', { name: 'Add' }))
  await user.type(screen.getByLabelText('New option'), 'Small form factor')
  await user.click(screen.getByRole('button', { name: 'Add' }))
  await user.click(screen.getByRole('button', { name: 'Create field' }))

  await waitFor(() => expect(assetsApi.createCustomField).toHaveBeenCalledWith({
    ciType: 'Hardware',
    key: 'form_factor',
    label: 'Form factor',
    type: 'Select',
    isRequired: false,
    options: ['Tower', 'Small form factor'],
  }))
})

/** A choose-one field with nothing to choose from is not a filter; it must not be submittable. */
test('a choose-one field cannot be created without options', async () => {
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'New field' }))
  await user.type(screen.getByLabelText('Label'), 'Form factor')
  await user.type(screen.getByLabelText('Key'), 'form_factor')

  expect(screen.getByRole('button', { name: 'Create field' })).toBeDisabled()
})

test('an option can be removed before the field is created', async () => {
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'New field' }))
  await user.type(screen.getByLabelText('New option'), 'Tower')
  await user.click(screen.getByRole('button', { name: 'Add' }))
  await user.click(screen.getByRole('button', { name: 'Remove Tower' }))

  expect(screen.queryByRole('button', { name: 'Remove Tower' })).not.toBeInTheDocument()
})

/** FAILURE PATH: a field holding values on real assets cannot be deleted, and the server says so. */
test('a field already in use reports the refusal and stays listed', async () => {
  vi.mocked(assetsApi.deleteCustomField).mockRejectedValue(
    new ApiError(409, 'Fields that already hold values cannot be deleted.'))
  vi.spyOn(window, 'confirm').mockReturnValue(true)
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'Delete Hardware type' }))

  await waitFor(() => expect(toasts.error)
    .toHaveBeenCalledWith('Fields that already hold values cannot be deleted.'))
  expect(screen.getByText('Hardware type')).toBeInTheDocument()
})

/** FAILURE PATH: a duplicate key belongs beside the form, not in a toast. */
test('a duplicate key keeps the dialog open with the reason in it', async () => {
  vi.mocked(assetsApi.createCustomField).mockRejectedValue(
    new ApiError(409, "A field with key 'hardware_type' already exists."))
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'New field' }))
  await user.type(screen.getByLabelText('Label'), 'Hardware type')
  await user.type(screen.getByLabelText('Key'), 'hardware_type')
  await user.type(screen.getByLabelText('New option'), 'Laptop')
  await user.click(screen.getByRole('button', { name: 'Add' }))
  await user.click(screen.getByRole('button', { name: 'Create field' }))

  expect(await screen.findByRole('alert')).toHaveTextContent("A field with key 'hardware_type' already exists.")
  expect(screen.getByRole('dialog')).toBeInTheDocument()
})

/** Marking a field required affects assets that already exist, and that must be said out loud. */
test('warns that requiring a field affects existing assets', async () => {
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'New field' }))
  await user.click(screen.getByLabelText(/Required on every/))

  expect(screen.getByText(/Existing assets have no value for this field/)).toBeInTheDocument()
})

/** The edit people actually want: more options on a field that already exists. */
test('an existing field can be given more options', async () => {
  vi.mocked(assetsApi.updateCustomField).mockResolvedValue(hardwareType)
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'Edit Hardware type' }))
  await user.type(screen.getByLabelText('New option'), 'Tablet')
  await user.click(screen.getByRole('button', { name: 'Add' }))
  await user.click(screen.getByRole('button', { name: 'Save field' }))

  await waitFor(() => expect(assetsApi.updateCustomField).toHaveBeenCalledWith('field-kind', {
    id: 'field-kind',
    key: 'hardware_type',
    label: 'Hardware type',
    type: 'Select',
    isRequired: false,
    options: ['Laptop', 'Desktop', 'Printer', 'Tablet'],
    sortOrder: 0,
  }))
})

/** The key is the name every stored value and every import refers to the field by. */
test('the key and the kind cannot be changed on an existing field', async () => {
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'Edit Hardware type' }))

  expect(screen.getByLabelText('Key')).toHaveAttribute('readonly')
  expect(screen.getByLabelText('Kind')).toBeDisabled()
})

/**
 * An option assets are recorded as cannot be removed — the server refuses it, and the dialog says so
 * before anybody tries, with the count.
 */
test('an option the estate still uses cannot be removed, and says how many use it', async () => {
  vi.mocked(assetsApi.getCustomFieldValueCounts).mockResolvedValue([
    { value: 'Printer', ciCount: 2 },
  ])
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'Edit Hardware type' }))

  const inUse = await screen.findByRole('button', { name: 'Printer, used by 2 assets' })
  expect(inUse).toBeDisabled()
  // One nothing is recorded as stays removable.
  expect(screen.getByRole('button', { name: 'Remove Laptop' })).toBeEnabled()
})

/** FAILURE PATH: the server is the authority, and its refusal belongs beside the form. */
test('a refused option removal keeps the dialog open with the reason in it', async () => {
  vi.mocked(assetsApi.updateCustomField).mockRejectedValue(new ApiError(409,
    "Configuration items still hold options you are removing (2 on 'Printer'). Change those first."))
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'Edit Hardware type' }))
  await user.click(screen.getByRole('button', { name: 'Remove Printer' }))
  await user.click(screen.getByRole('button', { name: 'Save field' }))

  expect(await screen.findByRole('alert')).toHaveTextContent("2 on 'Printer'")
  expect(screen.getByRole('dialog')).toBeInTheDocument()
})

/** The key follows the label, in the format the server accepts. */
test('the key is generated from the label', async () => {
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'New field' }))
  await user.type(screen.getByLabelText('Label'), 'Warranty provider')

  expect(screen.getByLabelText('Key')).toHaveValue('warranty_provider')
})

/**
 * Once somebody sets the key themselves it stops following, and a later edit to the label must not
 * overwrite it — the key is permanent, so a chosen one has to survive.
 */
test('a key typed by hand stops following the label', async () => {
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'New field' }))
  await user.type(screen.getByLabelText('Label'), 'Warranty provider')
  await user.clear(screen.getByLabelText('Key'))
  await user.type(screen.getByLabelText('Key'), 'vendor')

  await user.type(screen.getByLabelText('Label'), ' and terms')

  expect(screen.getByLabelText('Key')).toHaveValue('vendor')
  expect(screen.getByLabelText('Label')).toHaveValue('Warranty provider and terms')
})

test('a generated key is sent with the field', async () => {
  vi.mocked(assetsApi.createCustomField).mockResolvedValue(hardwareType)
  const user = userEvent.setup()
  renderPage()
  await screen.findByText('Hardware type')

  await user.click(screen.getByRole('button', { name: 'New field' }))
  await user.type(screen.getByLabelText('Label'), 'Screen size (inches)')
  await user.type(screen.getByLabelText('New option'), '14')
  await user.click(screen.getByRole('button', { name: 'Add' }))
  await user.click(screen.getByRole('button', { name: 'Create field' }))

  await waitFor(() => expect(assetsApi.createCustomField).toHaveBeenCalledWith(
    expect.objectContaining({ key: 'screen_size_inches', label: 'Screen size (inches)' })))
})
