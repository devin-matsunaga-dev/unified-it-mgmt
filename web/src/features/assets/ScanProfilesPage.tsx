import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowRight, CircleCheck, CircleSlash, Clock, Loader2, Pencil, Play, Plus, Radar, Trash2, TriangleAlert } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import {
  scanProfilesApi,
  type ScanProfile,
  type ScanRun,
  type ScanRunStatus,
} from '../../api/monitoring'
import { ApiError } from '../../api/client'
import { Button } from '../../components/ui/Button'
import { usePageHeading } from '../../layout/pageHeading'
import { DiscoveryTabs } from './DiscoveryTabs'
import { ScanProfileDialog } from './ScanProfileDialog'

/**
 * Where the scanners look — the configuration half of Discovery, which had no browser surface at all
 * until now: `/api/scan-profiles` has been a complete CRUD API since WP-4.1 that nothing in the SPA
 * called, so the only two profiles that could exist were the seeded ones.
 *
 * A scan is *requested*, never started from here. ARCHITECTURE §4 forbids pushing a command at an
 * agent, so the button writes a row and the scanner collects it on its own next cycle — which is why
 * the button says "Scan now" and the status that follows says "Queued".
 */
export function ScanProfilesPage() {
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState<ScanProfile | 'new' | null>(null)
  const [confirmingDelete, setConfirmingDelete] = useState<string | null>(null)

  usePageHeading({ title: 'Scan profiles', subtitle: 'Where the scanners look, how often, and how hard.' })

  const profiles = useQuery({
    queryKey: ['scan-profiles'],
    queryFn: () => scanProfilesApi.list({ pageSize: 200 }),
    placeholderData: keepPreviousData,
  })

  const settings = useQuery({
    queryKey: ['discovery-settings'],
    queryFn: () => scanProfilesApi.getSettings(),
  })

  // Polled rather than pushed: there is no SignalR hub for discovery. The interval tightens while
  // anything is in flight, because that is when the progress line is worth watching and when a
  // ten-second refresh would make a sweep that takes fifteen seconds look like two frames.
  const runs = useQuery({
    queryKey: ['scan-runs'],
    queryFn: () => scanProfilesApi.listRuns({ pageSize: 50 }),
    refetchInterval: (query) => query.state.data?.items.some(inFlight) ? 2_000 : 10_000,
    placeholderData: keepPreviousData,
  })

  const scanNow = useMutation({
    mutationFn: (profileId: string) => scanProfilesApi.requestRun(profileId),
    onSuccess: async (run) => {
      await queryClient.invalidateQueries({ queryKey: ['scan-runs'] })
      toast.success(`${run.scanProfileName} is queued. A scanner picks it up within a cycle.`)
    },
    onError: (error: ApiError) => toast.error(error.message),
  })

  const remove = useMutation({
    mutationFn: (profileId: string) => scanProfilesApi.remove(profileId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['scan-profiles'] })
      await queryClient.invalidateQueries({ queryKey: ['scan-runs'] })
      setConfirmingDelete(null)
      toast.success('Scan profile deleted.')
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const setScheduled = useMutation({
    mutationFn: (enabled: boolean) => scanProfilesApi.updateSettings(enabled),
    onSuccess: async (updated) => {
      await queryClient.invalidateQueries({ queryKey: ['discovery-settings'] })
      toast.success(updated.scheduledScanningEnabled
        ? 'Scheduled scanning is back on.'
        : 'Scheduled scanning is off. Profiles can still be scanned on demand.')
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const scheduledScanningEnabled = settings.data?.scheduledScanningEnabled ?? true
  const latestRunFor = (profileId: string) =>
    runs.data?.items.find((run) => run.scanProfileId === profileId)

  return <div className="space-y-6">
    <DiscoveryTabs right={<Button onClick={() => setEditing('new')}>
      <Plus size={16} className="mr-1.5" />New profile
    </Button>} />

    <section className={`rounded-xl border p-4 ${scheduledScanningEnabled
      ? 'border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900'
      : 'border-amber-200 bg-amber-50 dark:border-amber-900 dark:bg-amber-950'}`}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-900 dark:text-slate-100">Scheduled scanning</h2>
          <p className="mt-1 text-[13px] text-slate-500">
            {scheduledScanningEnabled
              ? 'Every enabled profile runs on its own interval.'
              : 'Switched off for the whole estate. Nothing runs on a timer; "Scan now" still works.'}
          </p>
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={scheduledScanningEnabled} disabled={settings.isLoading || setScheduled.isPending}
            onChange={(event) => setScheduled.mutate(event.target.checked)} />
          Run profiles on their intervals
        </label>
      </div>
    </section>

    {profiles.isLoading && <p className="text-sm text-slate-500">Loading scan profiles…</p>}
    {profiles.isError && <p role="alert" className="text-sm text-red-600">{(profiles.error as Error).message}</p>}
    {profiles.data?.items.length === 0 && <EmptyState onCreate={() => setEditing('new')} />}

    <div className="space-y-3">
      {profiles.data?.items.map((profile) => <ProfileCard key={profile.id} profile={profile}
        run={latestRunFor(profile.id)}
        scheduledScanningEnabled={scheduledScanningEnabled}
        scanning={scanNow.isPending && scanNow.variables === profile.id}
        confirmingDelete={confirmingDelete === profile.id}
        deleting={remove.isPending && remove.variables === profile.id}
        onScan={() => scanNow.mutate(profile.id)}
        onEdit={() => setEditing(profile)}
        onDelete={() => confirmingDelete === profile.id ? remove.mutate(profile.id) : setConfirmingDelete(profile.id)}
        onCancelDelete={() => setConfirmingDelete(null)} />)}
    </div>

    {editing && <ScanProfileDialog profile={editing === 'new' ? null : editing}
      onClose={() => setEditing(null)}
      onSaved={async () => {
        setEditing(null)
        await queryClient.invalidateQueries({ queryKey: ['scan-profiles'] })
      }} />}
  </div>
}

function ProfileCard({ profile, run, scheduledScanningEnabled, scanning, confirmingDelete, deleting,
  onScan, onEdit, onDelete, onCancelDelete }: {
  profile: ScanProfile
  run: ScanRun | undefined
  scheduledScanningEnabled: boolean
  scanning: boolean
  confirmingDelete: boolean
  deleting: boolean
  onScan: () => void
  onEdit: () => void
  onDelete: () => void
  onCancelDelete: () => void
}) {
  // Three states, not two: a profile can be scheduled, on-demand only, or switched off entirely, and
  // the estate switch overrides the first without changing what the profile says about itself.
  const cadence = !profile.isEnabled ? 'Disabled'
    : !profile.scheduleEnabled ? 'On demand only'
      : scheduledScanningEnabled ? `Every ${profile.intervalMinutes} min`
        : `Every ${profile.intervalMinutes} min — paused`

  return <article className="rounded-xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <h3 className="font-medium text-slate-900 dark:text-slate-100">{profile.name}</h3>
          <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${cadenceTone(profile, scheduledScanningEnabled)}`}>
            {cadence}
          </span>
          {profile.discoveryGroup !== 'default' && <span className="rounded-md bg-slate-100 px-2 py-0.5 text-xs text-slate-600 dark:bg-slate-800 dark:text-slate-300">
            {profile.discoveryGroup}
          </span>}
        </div>
        {profile.description && <p className="mt-1 text-[13px] text-slate-500">{profile.description}</p>}
        <p className="mt-2 font-mono text-[13px] text-slate-600 dark:text-slate-300">{profile.ranges.join(', ')}</p>
        <p className="mt-1 text-xs text-slate-500">
          {profile.addressCount === null
            // `local` resolves on the scanner, against whatever subnet it is attached to.
            ? 'Size known only to the scanner'
            : `${profile.addressCount.toLocaleString()} address${profile.addressCount === 1 ? '' : 'es'}`}
          {' · '}
          {profile.ports.length === 0 ? 'ping only' : `${profile.ports.length} port${profile.ports.length === 1 ? '' : 's'}`}
          {profile.snmpEnabled && ' · SNMP'}
          {profile.neighbourDiscoveryEnabled && ' · LLDP/CDP'}
        </p>
      </div>

      <div className="flex shrink-0 items-center gap-2">
        <Button variant="secondary" onClick={onScan} disabled={scanning || !profile.isEnabled}>
          {scanning ? <Loader2 size={16} className="mr-1.5 animate-spin" /> : <Play size={16} className="mr-1.5" />}
          Scan now
        </Button>
        <Button variant="ghost" aria-label={`Edit ${profile.name}`} onClick={onEdit}><Pencil size={16} /></Button>
        {confirmingDelete
          ? <>
            <Button variant="secondary" className="border-red-200 text-red-700 hover:bg-red-50 dark:border-red-900 dark:text-red-400 dark:hover:bg-red-950"
              onClick={onDelete} disabled={deleting}>Delete</Button>
            <Button variant="ghost" onClick={onCancelDelete}>Cancel</Button>
          </>
          : <Button variant="ghost" aria-label={`Delete ${profile.name}`} onClick={onDelete}><Trash2 size={16} /></Button>}
      </div>
    </div>

    {run && <RunLine run={run} />}
  </article>
}

/** The last run of this profile, in one line. Zero devices is a result, so it is stated rather than hidden. */
function RunLine({ run }: { run: ScanRun }) {
  const { icon: Icon, tone, text } = runSummary(run)
  return <p className={`mt-3 flex flex-wrap items-center gap-2 border-t border-slate-100 pt-3 text-[13px] dark:border-slate-800 ${tone}`}>
    <Icon size={15} className={run.status === 'Running' ? 'animate-spin' : undefined} />
    {text}
    {/* What a scan is *for*. Without this the count is the end of the trail, and the queue holding
        the devices it found is a tab away with nothing pointing at it. */}
    {run.status === 'Succeeded' && (run.devicesFound ?? 0) > 0 && <Link to="/assets/discovery"
      className="font-medium text-blue-600 hover:underline">
      Open the review queue<ArrowRight size={14} className="ml-1 inline" />
    </Link>}
  </p>
}

/** Queued or running: the two states worth refreshing quickly for. */
function inFlight(run: ScanRun) {
  return run.status === 'Queued' || run.status === 'Running'
}

function runSummary(run: ScanRun) {
  switch (run.status) {
    case 'Queued':
      return { icon: Clock, tone: 'text-slate-500', text: 'Queued — a scanner collects it within a cycle.' }
    case 'Running':
      return { icon: Loader2, tone: 'text-blue-600', text: progressText(run) }
    case 'Succeeded':
      return {
        icon: CircleCheck,
        tone: 'text-slate-500',
        text: `Last scan probed ${run.addressesProbed ?? 0} addresses and found ${run.devicesFound ?? 0}.`,
      }
    case 'Failed':
      return { icon: TriangleAlert, tone: 'text-red-600', text: run.error ?? 'The last scan failed.' }
    case 'TimedOut':
      return {
        icon: CircleSlash,
        tone: 'text-amber-600',
        text: `No result from ${run.discoveryName ?? 'the scanner'} — it may be down.`,
      }
  }
}

/**
 * The evidence that a sweep is really happening: how far it has got, and the last address that
 * answered. Not "scanning 10.0.0.5 now" — the sweep runs hundreds of probes concurrently, so there is
 * no single current address and inventing one would be theatre.
 */
function progressText(run: ScanRun) {
  const on = run.discoveryName ?? 'a scanner'
  if (run.addressesProbed === null) return `Starting on ${on}…`

  const swept = run.addressesTotal === null
    ? `Swept ${run.addressesProbed.toLocaleString()} addresses`
    : `Swept ${run.addressesProbed.toLocaleString()} of ${run.addressesTotal.toLocaleString()}`
  const answered = run.lastRespondingAddress === null
    ? 'nothing has answered yet'
    : `last answered ${run.lastRespondingAddress}`
  return `${swept} on ${on} · ${answered}`
}

function cadenceTone(profile: ScanProfile, scheduledScanningEnabled: boolean) {
  if (!profile.isEnabled) return 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400'
  if (!profile.scheduleEnabled) return 'bg-blue-100 text-blue-700 dark:bg-blue-500/15 dark:text-blue-400'
  if (!scheduledScanningEnabled) return 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400'
  return 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400'
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return <div className="rounded-xl border border-dashed border-slate-300 p-10 text-center dark:border-slate-700">
    <Radar size={28} className="mx-auto text-slate-400" />
    <p className="mx-auto mt-4 max-w-md text-sm text-slate-500">
      No scan profile exists yet, so no scanner has anywhere to look. A profile is a range, an
      interval, and how hard to interrogate whatever answers.
    </p>
    <Button className="mt-4" onClick={onCreate}><Plus size={16} className="mr-1.5" />New profile</Button>
  </div>
}
