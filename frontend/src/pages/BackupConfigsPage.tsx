import { Fragment, useEffect, useMemo, useRef, useState } from 'react'
import { accountsApi, type Account } from '../api/accounts'
import { ApiError } from '../api/client'
import { refreshKeyringStatus, useKeyringStatus } from '../api/keyring'
import { settingsApi, type GlobalSettings } from '../api/settings'
import { ChangeLocalRootDialog } from '../components/ChangeLocalRootDialog'
import { DefaultableField } from '../components/DefaultableField'
import { PathBrowser } from '../components/PathBrowser'
import { RestoreDialog } from '../components/RestoreDialog'
import { ScopeTree } from '../components/ScopeTree'
import { StopBackupDialog } from '../components/StopBackupDialog'
import { formatBytes, formatDuration, formatVersionSpan } from '../constants/format'
import { Field } from '../components/Field'
import { showsInterruptedNotice } from '../lib/interruptedNotice'
import { latestWins } from '../lib/latestWins'
import { isInScope, parseScope, scopeToText } from '../lib/scopeRules'
import { runTotals } from '../lib/runSummary'
import { stageLines } from '../lib/stageLines'
import { Modal } from '../components/Modal'
import {
  activityBadgeLabels,
  activityLabels,
  backupConfigsApi,
  StorageTier,
  RetentionMode,
  CloudCheckLevel,
  LocalCheckLevel,
  CloudState,
  LocalState,
  BackupStatus,
  BackupStage,
  tierLabels,
  retentionModeLabels,
  backupStageLabels,
  type BackupActivity,
  type BackupConfig,
  type BackupConfigInput,
  type BackupRun,
  type BackupVersionInfo,
  type InterruptedRun,
  type StageProgress,
  type RestoreRun,
  type CheckRun,
  type RepairRun,
} from '../api/backupConfigs'
import {
  containersApi,
  validateContainerName,
  containerNameRule,
  BackupPresence,
  type ContainerInfo,
} from '../api/containers'

const cloudLevelLabels: Record<number, string> = {
  [CloudCheckLevel.None]: "Don't check cloud",
  [CloudCheckLevel.Metadata]: 'Metadata vs local cache',
  [CloudCheckLevel.ExistenceSize]: 'Existence + size',
  [CloudCheckLevel.Content]: 'Content (download + hash)',
}
const localLevelLabels: Record<number, string> = {
  [LocalCheckLevel.None]: "Don't check local",
  [LocalCheckLevel.Attributes]: 'Existence + size + permissions',
  [LocalCheckLevel.Content]: 'Content hash',
}
const cloudStateLabel = (s: number) =>
  s === CloudState.Ok ? 'OK' : s === CloudState.MissingOrBad ? 'MISSING/BAD' : '—'
const localStateLabel = (s: number) =>
  s === LocalState.Ok ? 'OK' : s === LocalState.Missing ? 'missing' : s === LocalState.Changed ? 'changed' : '—'

const MB = 1024 * 1024

const emptyForm: BackupConfigInput = {
  accountId: 0,
  containerName: '',
  name: '',
  description: '',
  localRoot: '',
  password: '',
  indexTier: StorageTier.Hot,
  dataTier: StorageTier.Hot,
  ignoreRules: null,
  dontCompressRules: null,
  ignoreRulesCaseInsensitive: null,
  dontCompressRulesCaseInsensitive: null,
  dontGroupRulesCaseInsensitive: null,
  crossDirGroupRulesCaseInsensitive: null,
  dontGroupRules: null,
  crossDirGroupRules: null,
  scopeRules: null,
  includeSymlinks: null,
  maxVersions: null,
  maxAgeDays: null,
  retentionMode: null,
  singleFileThresholdBytes: null,
  groupCapBytes: null,
  volumeBytes: null,
  verboseLogging: null,
}

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms))

// Drop one entry from the "config id → run state" map. Returns the original when absent, to avoid a pointless re-render.
function without<T>(map: Record<number, T>, id: number): Record<number, T> {
  if (!(id in map)) return map
  const next = { ...map }
  delete next[id]
  return next
}

// How long until the next retry. This changes every second, but the row already re-renders every second
// (polling), so no extra timer is started.
// Past due it says "now" rather than showing a negative — the actual release still waits for the current
// tick to reach the gate.
function formatRetryIn(at: string): string {
  const seconds = Math.round((new Date(at).getTime() - Date.now()) / 1000)
  return seconds <= 0 ? 'now' : `in ${formatDuration(seconds)}`
}

export function BackupConfigsPage() {
  const [configs, setConfigs] = useState<BackupConfig[]>([])
  const [accounts, setAccounts] = useState<Account[]>([])
  const [runs, setRuns] = useState<Record<number, BackupRun>>({})
  // Which runs have been asked to wind down, and how. Stopping is asynchronous — the signal only takes effect
  // at the next cancellation checkpoint, and the row refreshes on a 5-second poll — so without this the UI says
  // nothing at all between the click and the run actually settling, which reads as "the button is broken".
  const [windingDown, setWindingDown] = useState<Record<number, 'suspend' | 'stop'>>({})
  const [restores, setRestores] = useState<Record<number, RestoreRun>>({})
  const [repairs, setRepairs] = useState<Record<number, RepairRun>>({})
  const [checks, setChecks] = useState<Record<number, CheckRun>>({})
  // Interrupted runs left on disk, keyed by config id. After a restart nothing is in memory, so this is the only way to know work was left unfinished.
  const [interrupted, setInterrupted] = useState<Record<number, InterruptedRun[]>>({})
  // The configuration whose stop dialog is open.
  const [stopping, setStopping] = useState<BackupConfig | null>(null)
  const [checkModal, setCheckModal] = useState<BackupConfig | null>(null)
  const [restoreModal, setRestoreModal] = useState<BackupConfig | null>(null)
  const [deleteModal, setDeleteModal] = useState<BackupConfig | null>(null)
  const [errorModal, setErrorModal] = useState<BackupConfig | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [browsing, setBrowsing] = useState(false)
  const [changingRoot, setChangingRoot] = useState(false)
  const [editing, setEditing] = useState<BackupConfig | null>(null)
  const [step, setStep] = useState<1 | 2>(1)
  const [form, setForm] = useState<BackupConfigInput>(emptyForm)
  // Kept separate from form: it exists only for comparison and must never be POSTed along with it.
  const [passwordConfirm, setPasswordConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [postCreate, setPostCreate] = useState<BackupConfig | null>(null)
  const [resettingPassword, setResettingPassword] = useState<BackupConfig | null>(null)
  const keyring = useKeyringStatus()

  // Re-fetch the interrupted runs for the current configuration list. One configuration failing does not
  // interrupt the page: treat it as absent and try again next round. Besides load(), the unattended
  // 5-second refresh must take this path too — otherwise, when the process restarts while the page is
  // open, the in-memory run states are gone while interrupted is only updated on mount and on user
  // action, so the interrupted-run notice never appears and only a full page reload reveals it.
  // The two triggers (load() from a user action, and the unattended 5-second poll) leave two requests in
  // flight, and both overwrite the same state wholesale. Which returns first is not decided by which
  // started first, so a "only the latest may write" gate is required, or an old snapshot overwrites a new
  // one and the UI keeps showing an interrupted run that no longer exists until the next tick. Why not a
  // cancelled flag: see the note in latestWins.
  const interruptedGate = useRef(latestWins())
  const refreshInterrupted = (list: BackupConfig[]) => {
    const isLatest = interruptedGate.current.begin()
    void Promise.all(
      list.map(async (c) => [c.id, await backupConfigsApi.interrupted(c.id).catch(() => [])] as const),
    ).then((pairs) => {
      if (isLatest()) setInterrupted(Object.fromEntries(pairs))
    })
  }

  const load = () => {
    backupConfigsApi
      .list()
      .then((list) => {
        setConfigs(list)
        refreshInterrupted(list)
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }
  const [defaults, setDefaults] = useState<GlobalSettings | null>(null)
  // List the containers of the selected account (the PRD 1.2 endpoint, already used by ContainersPage).
  // Listing needs the cloud, and a failure must not block creating a backup — degrade to a plain text field.
  const [containerList, setContainerList] = useState<ContainerInfo[] | null>(null)
  const [containerListError, setContainerListError] = useState<string | null>(null)
  const [newContainer, setNewContainer] = useState(false)
  // Clear the marker once the backend agrees the run is over. Keyed off the polled status rather than a timer:
  // a run can take a while to reach its checkpoint, and guessing a duration would either flicker back to
  // "Suspend" while it is still winding down, or sit on "Suspending…" after it has finished.
  useEffect(() => {
    setWindingDown((m) => {
      const next = Object.fromEntries(
        Object.entries(m).filter(([id]) => runs[Number(id)]?.status === 'Running'))
      return Object.keys(next).length === Object.keys(m).length ? m : next
    })
  }, [runs])

  useEffect(load, [])

  // Lets the ticks and cleanups of the effects below read the **latest** configs/restores without putting
  // them in dependency arrays and rebuilding the interval — assigned during render, so after commit the
  // effects necessarily see the current values.
  const configsRef = useRef(configs)
  configsRef.current = configs
  const restoresRef = useRef(restores)
  restoresRef.current = restores

  // Pressing Edit expands the form under that row, which on a long list is somewhere below the fold — on a phone
  // several screens down. Scrolling targets the row, not the form: landing on the form's first field leaves "which
  // backup am I editing" off screen above it. A flag rather than a call inside startEdit, because the row has to be
  // committed to the DOM before anything can scroll to it.
  const editRowRef = useRef<HTMLTableRowElement | null>(null)
  const [pendingScrollToEdit, setPendingScrollToEdit] = useState(false)
  useEffect(() => {
    if (!pendingScrollToEdit) return
    setPendingScrollToEdit(false)
    editRowRef.current?.scrollIntoView({
      block: 'start',
      // An unrequested smooth scroll is exactly what "reduce motion" is asking not to happen.
      behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
    })
  }, [pendingScrollToEdit])

  // The list refreshes every 5 seconds: a purely local query (configuration rows plus in-memory
  // activity), no cloud.
  // This is an unattended background refresh, so one network blip must not raise an error banner — it
  // could cover another error the user is reading, and there is nothing they can do about this refresh
  // anyway. The next tick retries naturally.
  useEffect(() => {
    const refresh = () =>
      backupConfigsApi
        .list()
        .then((list) => {
          setConfigs(list)
          refreshInterrupted(list)
        })
        .catch(() => {})
    const t = setInterval(refresh, 5000)
    // Browsers throttle background-tab timers to minutes, so switching back shows the previous tick's
    // stale snapshot and waits a full period to update — with a long job running, that is half the reason
    // it "looks stuck".
    const onVisible = () => {
      if (document.visibilityState === 'visible') void refresh()
    }
    document.addEventListener('visibilitychange', onVisible)
    return () => {
      clearInterval(t)
      document.removeEventListener('visibilitychange', onVisible)
    }
  }, [])

  // While anything is active, poll only the active ones, and only the one endpoint matching that
  // activity. With everything idle, none of these requests are sent. This replaces the loop that lived in
  // a closure: the state comes from the server, so refreshing the page, switching tabs, or a backup
  // started by a scheduled task all look the same.
  // Every load() replaced the configuration list with a new array identity, which rebuilt this effect
  // every 5 seconds. A derived activeKey (only the ids and activities of active configurations) rebuilds
  // the interval only when the set of actually active configurations changes.
  const activeKey = configs
    .filter((c) => c.activity !== 'Idle')
    .map((c) => `${c.id}:${c.activity}`)
    .join(',')

  useEffect(() => {
    if (!activeKey) return

    let cancelled = false
    // Parse id and activity out of the activeKey string, avoiding a dependency on the configs array identity
    const activeList = activeKey.split(',').map((item) => {
      const [id, activity] = item.split(':')
      // Assert back to BackupActivity: without it, activity is a string and the literal comparisons below
      // stop being compiler-checked, so one mistyped letter silently stops polling that category instead
      // of failing to compile.
      return { id: Number(id), activity: activity as BackupActivity }
    })

    // tick is itself async: while one has not finished (a status request landing while 7z is saturating
    // the CPU can take longer than a second) the next must not stack on top — stacked requests queue
    // against the browser's per-origin limit and drag the 5-second list refresh down with them, stalling
    // the page exactly when it most needs to update.
    let inFlight = false

    const tick = async () => {
      if (inFlight) return
      inFlight = true
      try {
        await Promise.all(
          activeList.map(async (item) => {
            try {
              const tasks: Promise<void>[] = []
              if (item.activity === 'BackingUp') {
                tasks.push(
                  backupConfigsApi
                    .runStatus(item.id)
                    .then((s) => {
                      if (!cancelled) setRuns((r) => ({ ...r, [item.id]: s }))
                    })
                    .catch((e) => {
                      // 404 = the backend process restarted and no longer holds this run in memory, so
                      // showing the old progress would be a lie. Clearing runs[id] lets the next 5-second
                      // refresh pick it up from interrupted, and the row becomes the interrupted-run
                      // notice on its own. Anything other than 404 (a blip or other transient failure) is
                      // not cleared and is rethrown for the per-configuration catch below to swallow and
                      // retry next tick — otherwise one blip would wipe this row out.
                      if (e instanceof ApiError && e.status === 404) {
                        if (!cancelled) setRuns((r) => without(r, item.id))
                        return
                      }
                      throw e
                    }),
                )
                // activity is a single value, so a concurrent restore is masked by BackingUp (see the
                // comment at the top of RestoreRunner.cs — a restore does not take the busy lock and may
                // run alongside a backup). If this configuration is locally remembered as still restoring,
                // fetch the restore state independently regardless of what activity says, or the restore's
                // terminal state is never seen when the two finish together.
                if (restoresRef.current[item.id]?.status === 'Running') {
                  tasks.push(
                    backupConfigsApi.restoreStatus(item.id).then((s) => {
                      if (!cancelled) setRestores((r) => ({ ...r, [item.id]: s }))
                    }),
                  )
                }
              } else if (item.activity === 'Restoring') {
                tasks.push(
                  backupConfigsApi.restoreStatus(item.id).then((s) => {
                    if (!cancelled) setRestores((r) => ({ ...r, [item.id]: s }))
                  }),
                )
              } else if (item.activity === 'Repairing') {
                tasks.push(
                  backupConfigsApi.repairStatus(item.id).then((s) => {
                    if (!cancelled) setRepairs((r) => ({ ...r, [item.id]: s }))
                  }),
                )
              } else if (item.activity === 'Checking') {
                tasks.push(
                  backupConfigsApi.checkStatus(item.id).then((s) => {
                    if (!cancelled && s) setChecks((r) => ({ ...r, [item.id]: s }))
                  }),
                )
              }
              // CleaningUp has no status endpoint: show the badge only, fetch no progress.
              await Promise.all(tasks)
            } catch {
              // One failed poll is not worth interrupting the page; the next tick retries.
            }
          }),
        )
      } finally {
        inFlight = false
      }
    }

    const t = setInterval(tick, 1000)
    void tick()
    // As above: on returning to the foreground, tick immediately rather than leaving the user staring at a minute-old snapshot.
    const onVisible = () => {
      if (document.visibilityState === 'visible') void tick()
    }
    document.addEventListener('visibilitychange', onVisible)
    return () => {
      cancelled = true
      clearInterval(t)
      document.removeEventListener('visibilitychange', onVisible)

      // This 1-second tick and the 5-second list refresh above are independent in phase: if the latter is
      // what flipped a configuration's activity to Idle and dropped it out of the active set, this cleanup
      // runs before the next tick and the state freezes on the last non-terminal reading — the buttons are
      // clickable again while the row still says "Uploading 97%". So every configuration that genuinely
      // leaves (an active one no longer present in the latest configs) gets one final request rather than
      // simply being cancelled.
      // Deliberately not checking cancelled here: that flag only stops a stale periodic tick from writing
      // state, whereas this final request *is* the departure handling and must write its result.
      const stillActiveIds = new Set(configsRef.current.filter((c) => c.activity !== 'Idle').map((c) => c.id))
      activeList
        .filter((item) => !stillActiveIds.has(item.id))
        .forEach((item) => {
          if (item.activity === 'BackingUp') {
            void backupConfigsApi
              .runStatus(item.id)
              .then((s) => setRuns((r) => ({ ...r, [item.id]: s })))
              .catch((e) => {
                // As above: this departure may itself have been caused by a backend restart (the activity
                // read from configs is already back to Idle). On 404 the stale progress is cleared too;
                // other failures stay silent — a final request is best-effort and must not raise an error
                // that interrupts the page.
                if (e instanceof ApiError && e.status === 404) setRuns((r) => without(r, item.id))
              })
            if (restoresRef.current[item.id]?.status === 'Running') {
              void backupConfigsApi
                .restoreStatus(item.id)
                .then((s) => setRestores((r) => ({ ...r, [item.id]: s })))
                .catch(() => {})
            }
          } else if (item.activity === 'Restoring') {
            void backupConfigsApi
              .restoreStatus(item.id)
              .then((s) => setRestores((r) => ({ ...r, [item.id]: s })))
              .catch(() => {})
          } else if (item.activity === 'Repairing') {
            void backupConfigsApi
              .repairStatus(item.id)
              .then((s) => setRepairs((r) => ({ ...r, [item.id]: s })))
              .catch(() => {})
          } else if (item.activity === 'Checking') {
            void backupConfigsApi
              .checkStatus(item.id)
              .then((s) => { if (s) setChecks((r) => ({ ...r, [item.id]: s })) })
              .catch(() => {})
          }
        })
    }
  }, [activeKey])

  useEffect(() => {
    accountsApi.list().then(setAccounts).catch(() => {})
    settingsApi.get().then(setDefaults).catch(() => {})
  }, [])

  // In edit mode both account and container are locked, so there is nothing to list.
  useEffect(() => {
    if (editing || !showForm || !form.accountId) return
    let cancelled = false
    setContainerList(null)
    setContainerListError(null)
    containersApi
      .list(form.accountId)
      .then((list) => {
        if (!cancelled) setContainerList(list)
      })
      .catch((e) => {
        if (!cancelled) setContainerListError(e instanceof Error ? e.message : String(e))
      })
    return () => {
      cancelled = true
    }
  }, [form.accountId, editing, showForm])

  // Keyring-loss recovery (design §3.5): the ordering dependency is real — verifying a backup password
  // needs the cloud, and reaching the cloud needs the account key restored first.
  // While accounts still have pending resets, the reset button is disabled, so the user does not waste an
  // attempt on a backup password before the accounts are fixed.
  const accountsStillPending = (keyring?.accountsPending ?? 0) > 0

  // In recovery mode, backup/restore/check/repair all return 409 (design §3.3): the buttons are disabled
  // with the reason stated, rather than letting the user click and meet a raw 409 body.
  const keyringLost = keyring?.status === 'Lost'
  const keyringLostHint = keyringLost
    ? 'Data protection keys were lost — re-enter credentials before running this action.'
    : undefined

  const startResetPassword = (c: BackupConfig) => {
    setResettingPassword(c)
    setError(null)
  }

  const closeResetPassword = () => {
    setResettingPassword(null)
    setError(null)
  }

  const submitResetPassword = async (password: string) => {
    if (!resettingPassword) return
    setBusy(true)
    try {
      await backupConfigsApi.resetPassword(resettingPassword.id, password)
      setResettingPassword(null)
      load()
      void refreshKeyringStatus()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const set = <K extends keyof BackupConfigInput>(k: K, v: BackupConfigInput[K]) =>
    setForm((f) => ({ ...f, [k]: v }))

  const startNew = () => {
    setEditing(null)
    // The twelve inheritable fields stay null (= use default, PRD §3). The tiers are locked after
    // creation and not inheritable, so they are still prefilled from the global defaults and fixed on save.
    setForm({
      ...emptyForm,
      accountId: accounts[0]?.id ?? 0,
      ...(defaults && {
        indexTier: defaults.defaultIndexTier,
        dataTier: defaults.defaultDataTier,
      }),
    })
    setStep(1)
    setPasswordConfirm('')
    setError(null)
    // This flag is independent of form and is not cleared when the form resets; a stale true would wrongly put the container picker into free-text mode
    setNewContainer(false)
    setPickingScope(false)
    setShowForm(true)
  }

  const startEdit = (c: BackupConfig) => {
    setEditing(c)
    setForm({
      accountId: c.accountId,
      containerName: c.containerName,
      name: c.name,
      description: c.description ?? '',
      localRoot: c.localRoot,
      password: '',
      indexTier: c.indexTier,
      dataTier: c.dataTier,
      ignoreRules: c.ignoreRules,
      dontCompressRules: c.dontCompressRules,
      ignoreRulesCaseInsensitive: c.ignoreRulesCaseInsensitive,
      dontCompressRulesCaseInsensitive: c.dontCompressRulesCaseInsensitive,
      dontGroupRulesCaseInsensitive: c.dontGroupRulesCaseInsensitive,
      crossDirGroupRulesCaseInsensitive: c.crossDirGroupRulesCaseInsensitive,
      dontGroupRules: c.dontGroupRules,
      crossDirGroupRules: c.crossDirGroupRules,
      scopeRules: c.scopeRules,
      includeSymlinks: c.includeSymlinks,
      verboseLogging: c.verboseLogging,
      maxVersions: c.maxVersions,
      maxAgeDays: c.maxAgeDays,
      retentionMode: c.retentionMode,
      singleFileThresholdBytes: c.singleFileThresholdBytes,
      groupCapBytes: c.groupCapBytes,
      volumeBytes: c.volumeBytes,
    })
    setStep(1)
    setPasswordConfirm('')
    setError(null)
    setNewContainer(false)
    setPickingScope(!!c.scopeRules)
    setShowForm(true)
    setPendingScrollToEdit(true)
  }

  // While editing, the password field is locked and takes no part in the comparison. An empty password
  // (no encryption) requires the confirmation to be empty too, which also catches "meant to set a
  // password but only filled one box".
  const passwordMismatch = !editing && (form.password ?? '') !== passwordConfirm
  // The tree wants a rule set while the form stores text. Computed live rather than kept as a second copy of state.
  const scope = useMemo(() => parseScope(form.scopeRules), [form.scopeRules])
  // "Everything" and "an empty rule set" are both null as text, but the UI must tell them apart: the box
  // ticked is the former, unticked and starting from everything selected is the latter. Hence this toggle
  // has to be independent of form.
  const [pickingScope, setPickingScope] = useState(false)

  const save = async () => {
    if (passwordMismatch) return
    // The narrowing warning. Files moved out of scope are treated as deleted by the next backup — new
    // versions no longer contain them (old versions still restore, until retention removes them). Same
    // behaviour as changing the ignore rules, but a few clicks on the tree can narrow a great deal, so it
    // has to be said here.
    if (editing) {
      const before = parseScope(editing.scopeRules)
      const after = parseScope(form.scopeRules)
      // Judged by the difference between the old and new rule sets, without touching the filesystem: if
      // any path named by either side's rules goes from in scope to out, that counts as narrowing. The
      // paths the rules name are exactly the boundary points where scope changes, so that suffices.
      const boundaries = new Set<string>([...before.keys(), ...after.keys()])
      const narrowed = [...boundaries].some((p) => isInScope(before, p) && !isInScope(after, p))
      if (
        narrowed
        && !window.confirm(
          'This narrows the backup scope. Files that are no longer in scope will be treated as '
            + 'deleted on the next backup: new versions will not include them. Older versions keep '
            + 'them until your retention policy removes those versions. Continue?',
        )
      )
        return
    }
    setBusy(true)
    setError(null)
    try {
      if (editing) {
        await backupConfigsApi.update(editing.id, form)
      } else {
        // §4.6: after a successful creation, do not just close — offer to run the first backup now.
        const created = await backupConfigsApi.create(form)
        setPostCreate(created)
      }
      setShowForm(false)
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  // Failures are **not** swallowed here: the error has to appear inside the delete dialog (see
  // DeleteModal). Writing it to the page's global error puts it underneath the dialog, so what the user
  // sees is "I pressed Delete and nothing happened" — which is exactly what the backend refusing to
  // delete a running configuration (409) looked like every time.
  const remove = async (c: BackupConfig, deleteContainer: boolean) => {
    await backupConfigsApi.remove(c.id, deleteContainer)
    setDeleteModal(null)
    // The delete was started from this configuration's edit form: leaving that form open faces the user
    // with a configuration that no longer exists, and pressing Save again only hits a 404.
    if (editing?.id === c.id) {
      setShowForm(false)
      setEditing(null)
    }
    load()
  }

  const resetStatus = async (c: BackupConfig) => {
    try {
      await backupConfigsApi.resetStatus(c.id)
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  // Only starts it — polling is left to the unified activity-dispatched mechanism above (the server is the single source of truth).
  const run = async (c: BackupConfig) => {
    setError(null)
    try {
      const state = await backupConfigsApi.run(c.id)
      setRuns((r) => ({ ...r, [c.id]: state }))
      // The previous check/repair verdict describes "what is in the cloud right now", which this backup
      // invalidates the moment it uploads: leaving that green line in place only suggests it still holds.
      // It used to take navigating away and back (remounting the component) to clear it.
      setChecks((m) => without(m, c.id))
      setRepairs((m) => without(m, c.id))
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  // Stop one running operation. Before this, the only way to stop a backup that had been running for
  // hours was restarting the container — and the user runs on a NAS, where that takes other services down
  // with it, while "no deleting a configuration while busy" closed off deletion as an escape too.
  // Stopping per operation rather than all at once: a backup and a restore can run concurrently, and
  // stopping the wrong one costs the same hours.
  const stopOp = async (c: BackupConfig, what: 'backup' | 'restore' | 'repair' | 'check', label: string) => {
    // A backup has to ask whether the file currently uploading should be finished or dropped, which one confirm cannot express, so it opens a dialog.
    if (what === 'backup') {
      setStopping(c)
      return
    }
    if (!window.confirm(`Stop the running ${label} for "${c.name}"? Work done so far is kept, but the operation will not finish.`))
      return
    setError(null)
    try {
      await backupConfigsApi.cancel(c.id, what)
      // Cancelling is asynchronous: after the signal, the run only winds down at the next cancellation
      // checkpoint, so nothing is written here and the unified polling fetches the real terminal state
      // (Canceled).
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const suspendBackup = async (c: BackupConfig) => {
    setError(null)
    // Mark it before the request, not after: the whole complaint is that pressing Suspend looks like nothing
    // happened, and the request itself is the part that has already returned by the time anyone notices.
    setWindingDown((m) => ({ ...m, [c.id]: 'suspend' }))
    try {
      await backupConfigsApi.suspend(c.id)
      load()
    } catch (e) {
      setWindingDown((m) => {
        const { [c.id]: _dropped, ...rest } = m
        return rest
      })
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const retryNow = async (c: BackupConfig) => {
    setError(null)
    try {
      await backupConfigsApi.retryNow(c.id)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const discardInterrupted = async (c: BackupConfig) => {
    if (!window.confirm(
      `Discard the interrupted run for "${c.name}"? The blocks already uploaded stop being reserved and will be removed by the next cleanup, so the next backup re-uploads them.`))
      return
    setError(null)
    try {
      await backupConfigsApi.discardInterrupted(c.id)
      setRuns((m) => without(m, c.id))
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  // As above: write only the initial state; the unified polling takes over from there.
  const pollRestore = (id: number, state: RestoreRun) => {
    setRestores((r) => ({ ...r, [id]: state }))
    load()
  }

  const [importing, setImporting] = useState(false)
  const [importForm, setImportForm] = useState({
    accountId: 0,
    containerName: '',
    password: '',
    checkAfterImport: true,
  })
  const doImport = async () => {
    setError(null)
    try {
      const result = await backupConfigsApi.import(
        importForm.accountId || accounts[0]?.id || 0,
        importForm.containerName,
        importForm.password || null,
        importForm.checkAfterImport,
      )
      setImporting(false)
      setImportForm({ accountId: 0, containerName: '', password: '', checkAfterImport: true })
      load()
      if (result.unreadableVersions.length > 0) {
        setError(
          `Imported, but the file list of ${result.unreadableVersions
            .map((v) => `v${v}`)
            .join(', ')} could not be read. Those versions cannot be restored or checked.`,
        )
      }
      // The check is already running in the background, so put the panel in front of the user — making them hunt for that button again makes no sense.
      if (result.checkStarted) setCheckModal(result.config)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const accountName = (id: number) => accounts.find((a) => a.id === id)?.name ?? `#${id}`

  // editing is a snapshot from the moment Edit was pressed, and the form may stay open for minutes — so
  // activity has to come from the configs refreshed every 5 seconds, or the delete button's disabled state
  // freezes at the tick the form was opened.
  const editingLive = editing ? (configs.find((c) => c.id === editing.id) ?? editing) : null

  // Both steps share one action bar: deleting has nothing to do with which step you are on, and should
  // not force the user to go Back to step 1 first.
  // The backend already refuses to delete a running configuration (409, see DeriveActivity in
  // BackupConfigEndpoints); greying out here only aligns the button with that existing guard — the real
  // guarantee stays on the backend, because activity refreshes only every 5 seconds and the button is
  // still live for the first few seconds of a run (where the error surfaces in the dialog, see DeleteModal).
  const deleteButton = editingLive && (
    <button
      type="button"
      className="btn-danger"
      // Pushed to the far end of the action bar: an irreversible action should not sit next to Save/Cancel where it invites a misclick.
      style={{ marginLeft: 'auto' }}
      onClick={() => setDeleteModal(editingLive)}
      disabled={busy || editingLive.activity !== 'Idle'}
      title={
        editingLive.activity === 'Idle'
          ? undefined
          : `Currently ${activityLabels[editingLive.activity]} — stop it or wait for it to finish before deleting.`
      }
    >
      Delete…
    </button>
  )

  // The edit form is rendered in one of two places: under the row being edited, or -- for a new backup, which belongs to
  // no row -- under the table. Held in a variable rather than extracted into a component on purpose: it reads twenty-odd
  // pieces of page state (form, step, editing, accounts, containerList, scope, passwordConfirm, ...), every one of which
  // would have to become a prop, for no behaviour change whatsoever.
  const formPanel = showForm && (
    <div className="panel">
      <h2>
        {editing ? `Edit: ${editing.name}` : 'New Backup'} — Step {step} of 2
      </h2>

      {step === 1 ? (
        <>
          <Field label={editing ? 'Account (locked)' : 'Account'}>
            <select
              value={form.accountId}
              disabled={!!editing}
              onChange={(e) => {
                setForm((f) => ({ ...f, accountId: Number(e.target.value), containerName: '' }))
                setNewContainer(false)
              }}
            >
              {accounts.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label={editing ? 'Container (locked)' : 'Container'} multi>
            {editing || containerListError || containerList === null ? (
              <>
                <input
                  className="w-lg mono"
                  value={form.containerName}
                  disabled={!!editing}
                  onChange={(e) => set('containerName', e.target.value)}
                />
                {!editing && containerListError && (
                  <div className="text-warn">
                    Could not list containers ({containerListError}). Type the name instead.
                  </div>
                )}
                {!editing && !containerListError && containerList === null && (
                  <div className="text-faint">Loading containers…</div>
                )}
              </>
            ) : (
              <>
                <select
                  className="w-lg"
                  value={newContainer ? ' new' : form.containerName}
                  onChange={(e) => {
                    if (e.target.value === ' new') {
                      setNewContainer(true)
                      set('containerName', '')
                    } else {
                      setNewContainer(false)
                      set('containerName', e.target.value)
                    }
                  }}
                >
                  <option value="">— select —</option>
                  {containerList.map((c) => (
                    // A container already held by a backup is not selectable: the server would
                    // refuse it with 409 anyway, and two backups writing one container overwrite each
                    // other's version history while each one's data is deleted as an orphan by the
                    // other's retention cleanup. Occupancy comes from the local configuration, which
                    // holds true mid-backup too — the cloud info file is only written by the last step.
                    <option key={c.name} value={c.name} disabled={!!c.inUseBy}>
                      {c.name}
                      {c.inUseBy
                        ? `  ● in use by "${c.inUseBy}"`
                        : c.backup !== BackupPresence.None
                          ? '  ● has backup'
                          : ''}
                    </option>
                  ))}
                  <option value={' new'}>+ New container…</option>
                </select>
                {newContainer && (
                  <>
                    <input
                      className="w-lg mono"
                      placeholder="new-container-name"
                      value={form.containerName}
                      onChange={(e) => set('containerName', e.target.value)}
                    />
                    <div
                      className={
                        form.containerName && validateContainerName(form.containerName)
                          ? 'text-danger'
                          : 'text-faint'
                      }
                    >
                      {(form.containerName && validateContainerName(form.containerName)) ||
                        containerNameRule}
                    </div>
                  </>
                )}
              </>
            )}
          </Field>
          <Field label={editing ? 'Local Root (locked)' : 'Local Root'}>
            <input
              className="w-lg mono"
              placeholder="/data/photos"
              value={form.localRoot}
              disabled={!!editing}
              onChange={(e) => set('localRoot', e.target.value)}
            />
            {editing ? (
              // The root stays locked on the ordinary edit path; changing it goes through the dedicated validated channel (for when a mount point moves).
              <button type="button" onClick={() => setChangingRoot(true)}>
                Change…
              </button>
            ) : (
              <button type="button" onClick={() => setBrowsing(true)}>
                Browse
              </button>
            )}
          </Field>
          <Field label="Scope">
            <label className="row" style={{ gap: 'var(--sp-1)' }}>
              <input
                type="checkbox"
                checked={!pickingScope}
                disabled={!form.localRoot.trim()}
                onChange={(e) => {
                  setPickingScope(!e.target.checked)
                  // Both directions return to "everything": re-ticking clears the scope, unticking
                  // starts from everything selected and lets the user remove from there (design §10).
                  set('scopeRules', null)
                }}
              />
              <span>Back up everything in this folder</span>
            </label>
          </Field>
          {pickingScope && !!form.localRoot.trim() && (
            <>
              <p className="text-muted text-sm" style={{ margin: '0 0 var(--sp-2)' }}>
                Checking a folder backs up everything inside it, including files added later.
                Hidden files and files matched by the ignore rules are listed here too — ignore
                rules are applied separately and still leave those out of the backup.
              </p>
              <ScopeTree
                localRoot={form.localRoot}
                rules={scope}
                onChange={(next) => set('scopeRules', scopeToText(next) || null)}
                ignoreRules={form.ignoreRules ?? editing?.effective.ignoreRules ?? defaults?.defaultIgnoreRules ?? ''}
              />
            </>
          )}
          <Field label="Name">
            <input className="w-lg" value={form.name} onChange={(e) => set('name', e.target.value)} />
          </Field>
          <Field label="Description">
            <input
              className="w-lg"
              value={form.description ?? ''}
              onChange={(e) => set('description', e.target.value)}
            />
          </Field>
          <Field label={editing ? 'Password (locked)' : 'Password'}>
            <input
              type="password"
              className="w-lg"
              placeholder={
                editing
                  ? editing.hasPassword
                    ? 'Encrypted — cannot be changed after creation'
                    : 'Not encrypted — cannot be changed after creation'
                  : 'Optional — set to encrypt'
              }
              value={form.password ?? ''}
              disabled={!!editing}
              onChange={(e) => set('password', e.target.value)}
            />
          </Field>
          {/* Only creation asks twice. A password entered during import or keyring recovery is
              **verified** (a wrong one fails on the spot); this is the one place it is *set* — nothing
              can tell whether it is right, and afterwards it cannot be changed and losing it is
              unrecoverable. One mistyped invisible character surfaces only on the day a restore is
              actually needed. */}
          {!editing && (
            <Field label="Confirm password">
              <input
                type="password"
                className="w-lg"
                placeholder={form.password ? 'Re-enter the same password' : 'Leave empty for no encryption'}
                value={passwordConfirm}
                onChange={(e) => setPasswordConfirm(e.target.value)}
              />
            </Field>
          )}
          {passwordMismatch && (
            <div className="text-danger" style={{ marginBottom: '0.4rem' }}>
              Passwords do not match.
            </div>
          )}
          {!editing && !!form.password && !passwordMismatch && (
            <div className="text-warn" style={{ marginBottom: '0.4rem' }}>
              This password cannot be changed or recovered after the backup is created. If it is
              lost, nothing encrypted with it can be restored — the only way out is to delete the
              configuration and start over.
            </div>
          )}
          <Field label={editing ? 'Index Tier (locked)' : 'Index Tier'}>
            <TierSelect
              value={form.indexTier}
              onChange={(v) => set('indexTier', v)}
              archive={false}
              disabled={!!editing}
            />
          </Field>
          <Field label={editing ? 'Data Tier (locked)' : 'Data Tier'}>
            <TierSelect
              value={form.dataTier}
              onChange={(v) => set('dataTier', v)}
              archive
              disabled={!!editing}
            />
          </Field>

          <div className="row form-actions" style={{ marginTop: '1rem' }}>
            <button
              type="button"
              onClick={() => setStep(2)}
              disabled={
                !form.accountId ||
                !form.containerName.trim() ||
                !form.localRoot.trim() ||
                (newContainer && !!validateContainerName(form.containerName))
              }
            >
              Next
            </button>
            <button type="button" onClick={() => setShowForm(false)}>
              Cancel
            </button>
            {deleteButton}
          </div>
        </>
      ) : (
        <>
          {/* The path basis has to be stated here. Rules match paths **relative to Local Root**,
              excluding Local Root itself; without saying so it is natural to write them against the
              full path on screen, and every rule then matches nothing — silently, leaving the user to
              infer it from something as indirect as "the pack count did not go down". */}
          <p className="text-muted" style={{ margin: '0 0 var(--sp-2)' }}>
            All rule lists below use gitignore syntax and match paths <strong>relative to the local
            root</strong>{form.localRoot.trim() && <> (<span className="mono">{form.localRoot}</span>)</>} —
            write <span className="mono">/Backup/</span> to mean{' '}
            <span className="mono">{(form.localRoot.trim() || '<local root>').replace(/\/+$/, '')}/Backup</span>, not the
            full path. A trailing <span className="mono">/</span> means "this directory and everything under it".{' '}
            Each list has a second box below it matching the same syntax but ignoring case; the two form one
            list with the insensitive box last, so a rule there overrides a matching rule above it.
          </p>
          <DefaultableField
            label="Ignore rules"
            useDefault={form.ignoreRules === null}
            onToggle={(useDefault) =>
              set('ignoreRules', useDefault ? null : (editing?.effective.ignoreRules ?? defaults?.defaultIgnoreRules ?? ''))
            }
            effectiveText={(editing?.effective.ignoreRules ?? defaults?.defaultIgnoreRules) || '(none)'}
          >
            <RuleBox value={form.ignoreRules} onChange={(v) => set('ignoreRules', v)} />
            <CaseInsensitiveHalf
              value={form.ignoreRulesCaseInsensitive}
              onChange={(v) => set('ignoreRulesCaseInsensitive', v)}
            />
          </DefaultableField>
          <DefaultableField
            label="Don't compress"
            useDefault={form.dontCompressRules === null}
            onToggle={(useDefault) =>
              set(
                'dontCompressRules',
                useDefault ? null : (editing?.effective.dontCompressRules ?? defaults?.defaultDontCompressRules ?? ''),
              )
            }
            effectiveText={(editing?.effective.dontCompressRules ?? defaults?.defaultDontCompressRules) || '(none)'}
          >
            <RuleBox
              value={form.dontCompressRules}
              onChange={(v) => set('dontCompressRules', v)}
            />
            <CaseInsensitiveHalf
              value={form.dontCompressRulesCaseInsensitive}
              onChange={(v) => set('dontCompressRulesCaseInsensitive', v)}
            />
          </DefaultableField>
          <DefaultableField
            label="Don't group"
            useDefault={form.dontGroupRules === null}
            onToggle={(useDefault) =>
              set(
                'dontGroupRules',
                useDefault ? null : (editing?.effective.dontGroupRules ?? defaults?.defaultDontGroupRules ?? ''),
              )
            }
            effectiveText={(editing?.effective.dontGroupRules ?? defaults?.defaultDontGroupRules) || '(none)'}
          >
            <RuleBox value={form.dontGroupRules} onChange={(v) => set('dontGroupRules', v)} />
            <CaseInsensitiveHalf
              value={form.dontGroupRulesCaseInsensitive}
              onChange={(v) => set('dontGroupRulesCaseInsensitive', v)}
            />
          </DefaultableField>
          <DefaultableField
            label="Pack across directories"
            useDefault={form.crossDirGroupRules === null}
            onToggle={(useDefault) =>
              set(
                'crossDirGroupRules',
                useDefault ? null : (editing?.effective.crossDirGroupRules ?? defaults?.defaultCrossDirGroupRules ?? ''),
              )
            }
            effectiveText={(editing?.effective.crossDirGroupRules ?? defaults?.defaultCrossDirGroupRules) || '(none)'}
          >
            <RuleBox value={form.crossDirGroupRules} onChange={(v) => set('crossDirGroupRules', v)} />
            <CaseInsensitiveHalf
              value={form.crossDirGroupRulesCaseInsensitive}
              onChange={(v) => set('crossDirGroupRulesCaseInsensitive', v)}
            />
          </DefaultableField>
          <DefaultableField
            label="Include symlinks"
            useDefault={form.includeSymlinks === null}
            onToggle={(useDefault) =>
              set(
                'includeSymlinks',
                useDefault ? null : (editing?.effective.includeSymlinks ?? defaults?.defaultIncludeSymlinks ?? false),
              )
            }
            effectiveText={String(editing?.effective.includeSymlinks ?? defaults?.defaultIncludeSymlinks ?? false)}
          >
            <input
              type="checkbox"
              checked={form.includeSymlinks ?? false}
              onChange={(e) => set('includeSymlinks', e.target.checked)}
            />
          </DefaultableField>
          <DefaultableField
            label="Verbose (debug) logging"
            useDefault={form.verboseLogging === null}
            onToggle={(useDefault) =>
              set(
                'verboseLogging',
                useDefault ? null : (editing?.effective.verboseLogging ?? defaults?.defaultVerboseLogging ?? false),
              )
            }
            effectiveText={String(editing?.effective.verboseLogging ?? defaults?.defaultVerboseLogging ?? false)}
          >
            <input
              type="checkbox"
              checked={form.verboseLogging ?? false}
              onChange={(e) => set('verboseLogging', e.target.checked)}
            />
          </DefaultableField>
          <DefaultableField
            label="Max versions"
            useDefault={form.maxVersions === null}
            onToggle={(useDefault) =>
              set('maxVersions', useDefault ? null : (editing?.effective.maxVersions ?? defaults?.defaultMaxVersions ?? 100))
            }
            effectiveText={String(editing?.effective.maxVersions ?? defaults?.defaultMaxVersions ?? 100)}
          >
            <input
              className="w-sm"
              type="number"
              value={form.maxVersions ?? 0}
              onChange={(e) => set('maxVersions', Number(e.target.value))}
            />
          </DefaultableField>
          <DefaultableField
            label="Max age (days)"
            useDefault={form.maxAgeDays === null}
            onToggle={(useDefault) =>
              set('maxAgeDays', useDefault ? null : (editing?.effective.maxAgeDays ?? defaults?.defaultMaxAgeDays ?? 180))
            }
            effectiveText={String(editing?.effective.maxAgeDays ?? defaults?.defaultMaxAgeDays ?? 180)}
          >
            <input
              className="w-sm"
              type="number"
              value={form.maxAgeDays ?? 0}
              onChange={(e) => set('maxAgeDays', Number(e.target.value))}
            />
          </DefaultableField>
          <DefaultableField
            label="Retention mode"
            useDefault={form.retentionMode === null}
            onToggle={(useDefault) =>
              set(
                'retentionMode',
                useDefault ? null : (editing?.effective.retentionMode ?? defaults?.defaultRetentionMode ?? RetentionMode.EitherTriggers),
              )
            }
            effectiveText={
              retentionModeLabels[
                editing?.effective.retentionMode ?? defaults?.defaultRetentionMode ?? RetentionMode.EitherTriggers
              ]
            }
          >
            <select
              value={form.retentionMode ?? RetentionMode.EitherTriggers}
              onChange={(e) => set('retentionMode', Number(e.target.value))}
            >
              {Object.entries(retentionModeLabels).map(([v, label]) => (
                <option key={v} value={v}>
                  {label}
                </option>
              ))}
            </select>
          </DefaultableField>
          <DefaultableField
            label="Single-file threshold (MB)"
            useDefault={form.singleFileThresholdBytes === null}
            onToggle={(useDefault) =>
              set(
                'singleFileThresholdBytes',
                useDefault
                  ? null
                  : (editing?.effective.singleFileThresholdBytes ?? defaults?.defaultSingleFileThresholdBytes ?? 5 * MB),
              )
            }
            effectiveText={`${Math.round(
              (editing?.effective.singleFileThresholdBytes ?? defaults?.defaultSingleFileThresholdBytes ?? 5 * MB) / MB,
            )} MB`}
          >
            <input
              className="w-sm"
              type="number"
              value={Math.round((form.singleFileThresholdBytes ?? 0) / MB)}
              onChange={(e) => set('singleFileThresholdBytes', Number(e.target.value) * MB)}
            />
          </DefaultableField>
          <DefaultableField
            label="Group cap (MB)"
            useDefault={form.groupCapBytes === null}
            onToggle={(useDefault) =>
              set(
                'groupCapBytes',
                useDefault ? null : (editing?.effective.groupCapBytes ?? defaults?.defaultGroupCapBytes ?? 100 * MB),
              )
            }
            effectiveText={`${Math.round(
              (editing?.effective.groupCapBytes ?? defaults?.defaultGroupCapBytes ?? 100 * MB) / MB,
            )} MB`}
          >
            <input
              className="w-sm"
              type="number"
              value={Math.round((form.groupCapBytes ?? 0) / MB)}
              onChange={(e) => set('groupCapBytes', Number(e.target.value) * MB)}
            />
          </DefaultableField>
          <DefaultableField
            label="Volume size (MB, 0 = off)"
            useDefault={form.volumeBytes === null}
            onToggle={(useDefault) =>
              set('volumeBytes', useDefault ? null : (editing?.effective.volumeBytes ?? defaults?.defaultVolumeBytes ?? 0))
            }
            effectiveText={(() => {
              const bytes = editing?.effective.volumeBytes ?? defaults?.defaultVolumeBytes ?? 0
              // 0 = volume splitting off, a representation deliberately introduced by that round of work; this should read "off" rather than "0 MB".
              return bytes > 0 ? `${Math.round(bytes / MB)} MB` : 'off'
            })()}
          >
            <input
              className="w-sm"
              type="number"
              value={Math.round((form.volumeBytes ?? 0) / MB)}
              onChange={(e) => set('volumeBytes', Number(e.target.value) * MB)}
            />
          </DefaultableField>

          {error && <p className="text-danger">{error}</p>}
          <div className="row form-actions" style={{ marginTop: '1rem' }}>
            <button type="button" onClick={() => setStep(1)}>
              Back
            </button>
            <button type="button" className="btn-primary" onClick={save} disabled={busy || !form.name.trim() || passwordMismatch}>
              {editing ? 'Save' : 'Create'}
            </button>
            <button type="button" onClick={() => setShowForm(false)} disabled={busy}>
              Cancel
            </button>
            {deleteButton}
          </div>
        </>
      )}
    </div>
  )

  return (
    <section>
      <div className="page-header">
        <h1>Backups</h1>
        <div className="row">
          <button
            type="button"
            onClick={() => setImporting((v) => !v)}
            disabled={accounts.length === 0 || keyringLost}
            title={keyringLostHint}
          >
            Import existing
          </button>
          <button type="button" className="btn-primary" onClick={startNew} disabled={accounts.length === 0}>
            New Backup
          </button>
        </div>
      </div>

      {importing && (
        <div className="panel">
          <strong>Import existing backup</strong> (reads the container's info file)
          <div className="toolbar" style={{ marginTop: '0.5rem' }}>
            <select
              value={importForm.accountId || accounts[0]?.id || 0}
              onChange={(e) => setImportForm((f) => ({ ...f, accountId: Number(e.target.value) }))}
            >
              {accounts.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name}
                </option>
              ))}
            </select>
            <input
              placeholder="container name"
              value={importForm.containerName}
              onChange={(e) => setImportForm((f) => ({ ...f, containerName: e.target.value }))}
            />
            <input
              type="password"
              placeholder="password (if encrypted)"
              value={importForm.password}
              onChange={(e) => setImportForm((f) => ({ ...f, password: e.target.value }))}
            />
            <button type="button" onClick={doImport} disabled={!importForm.containerName}>
              Import
            </button>
          </div>
          <label className="row" style={{ gap: 'var(--sp-1)', marginTop: 'var(--sp-2)' }}>
            <input
              type="checkbox"
              checked={importForm.checkAfterImport}
              onChange={(e) =>
                setImportForm((f) => ({ ...f, checkAfterImport: e.target.checked }))
              }
            />
            {/* An import fetches the ledger; whether what the ledger lists still exists can only be answered by the cloud. HEAD only, no downloads. */}
            Check cloud data once the import finishes
          </label>
        </div>
      )}
      {accounts.length === 0 && <p className="text-muted">Add an account first.</p>}
      {/* While the form is open this moves inside it: the reason a save was rejected has to appear next to the
          button that was pressed, and this spot is above the table — which is now potentially many rows away
          from the form, since editing expands it inline. Left here, it would read as "I pressed it and nothing
          happened". */}
      {!showForm && error && <p className="text-danger">{error}</p>}

      {/* A backstop, not the main mechanism: table-fluid already pushes the table's minimum width down to
          ~656px, so normally no scrollbar appears here. It catches two corners — a window sitting exactly
          between 641 and 672px (before the card layout takes over), and an extremely long account or
          container name (a single word with no separators, which raises the minimum itself). With this
          layer the worst case is the table scrolling horizontally rather than the whole page.
          tabIndex is so keyboard users can scroll it (WCAG 2.1.1). */}
      <div className="table-scroll" tabIndex={0}>
        {/* cards = collapse into cards on phones; table-fluid = follow the window width on desktop (see the corresponding notes in index.css). */}
        <table className="cards table-fluid">
          {/* Only the two columns that need a fixed share on narrow screens are marked (see the column
              width notes in index.css); the rest are left to auto. A colgroup rather than classes on th:
              column width is a property of the column, and writing it here means adding or removing a
              column cannot miss it. */}
          <colgroup>
            <col />
            <col />
            <col className="col-root" />
            <col />
            <col />
            <col className="col-actions" />
          </colgroup>
          <thead>
            <tr>
              <th>Name</th>
              <th>Account / Container</th>
              <th>Local Root</th>
              <th>Encrypted</th>
              <th>Status</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {configs.length === 0 ? (
              <tr>
                <td colSpan={6} className="empty-state">
                  No backups yet.
                </td>
              </tr>
            ) : (
              configs.map((c) => {
                // The run status is moved out of the action column onto its own row (see .ops-row in
                // index.css): the action column is nowrap, and the path being processed is routinely
                // hundreds of characters, which would stretch the whole table off screen.
                const ops = [
                  runs[c.id] && (
                    <RunStatus
                      key="run"
                      run={runs[c.id]}
                      onStop={() => stopOp(c, 'backup', 'backup')}
                      onSuspend={() => void suspendBackup(c)}
                      onRetryNow={() => void retryNow(c)}
                      stopping={windingDown[c.id]}
                      onResume={() => void run(c)}
                      onDiscard={() => void discardInterrupted(c)}
                    />
                  ),
                  // A journal on disk that this row is not already offering a way out of — see
                  // showsInterruptedNotice for which run states those are. Show it and wait for the user,
                  // rather than deciding for them whether to continue.
                  showsInterruptedNotice(runs[c.id], interrupted[c.id]?.length ?? 0) && (
                    <InterruptedNotice
                      key="interrupted"
                      runs={interrupted[c.id]}
                      onResume={() => void run(c)}
                      onDiscard={() => void discardInterrupted(c)}
                    />
                  ),
                  restores[c.id] && <RestoreStatus key="restore" run={restores[c.id]} onStop={() => stopOp(c, 'restore', 'restore')} />,
                  repairs[c.id] && <RepairStatus key="repair" run={repairs[c.id]} onStop={() => stopOp(c, 'repair', 'repair')} />,
                  checks[c.id] && <CheckStatus key="check" run={checks[c.id]} onStop={() => stopOp(c, 'check', 'check')} />,
                ].filter(Boolean)
                return (
                <Fragment key={c.id}>
                <tr
                  ref={editing?.id === c.id ? editRowRef : undefined}
                  className={ops.length > 0 ? 'has-ops' : undefined}
                >
                  <td className="card-title">
                    {c.name}
                    {c.secretsUnavailable && (
                      <span className="row-inline" style={{ marginLeft: '0.5rem' }}>
                        <span className="text-warn">Password required</span>
                        <button
                          type="button"
                          onClick={() => startResetPassword(c)}
                          disabled={accountsStillPending}
                          title={accountsStillPending ? 'Re-enter account credentials first' : undefined}
                        >
                          Re-enter
                        </button>
                      </span>
                    )}
                  </td>
                  <td data-label="Account / Container">
                    {accountName(c.accountId)} / {c.containerName}
                  </td>
                  {/* cell-path allows breaking at any character (see index.css). For paths only — they
                      have no word boundaries to rely on, whereas breaking mid-word in other columns only
                      makes them harder to read. */}
                  <td className="mono text-faint cell-path" data-label="Local Root">{c.localRoot}</td>
                  <td data-label="Encrypted">{c.hasPassword ? 'Yes' : 'No'}</td>
                  <td data-label="Status">
                    <StatusBadge
                      config={c}
                      onReset={() => resetStatus(c)}
                      onShowError={() => setErrorModal(c)}
                    />
                  </td>
                  {/* Alignment and wrapping are left to .card-actions (index.css). This used to be an
                      inline style, and inline styles outrank any selector — so the rule switching to
                      left alignment in the phone card layout was mute all along, and the buttons stayed
                      crammed to the right on phones. */}
                  <td className="card-actions">
                    <button
                      type="button"
                      className="btn-ghost"
                      onClick={() => run(c)}
                      disabled={keyringLost || c.activity !== 'Idle'}
                      title={keyringLostHint}
                    >
                      {c.activity === 'BackingUp' ? 'Backing up…' : 'Backup'}
                    </button>{' '}
                    <button
                      type="button"
                      className="btn-ghost"
                      onClick={() => setRestoreModal(c)}
                      // Restore is not disabled by other activities: the backend deliberately allows a
                      // restore to run alongside a backup, check or repair without taking the busy lock
                      // (see the comment at the top of RestoreRunner.cs). Only an actual restore already
                      // running (Restoring) should grey this out — otherwise it stays grey all night
                      // during a scheduled backup.
                      disabled={keyringLost || c.activity === 'Restoring'}
                      title={keyringLostHint}
                    >
                      {c.activity === 'Restoring' ? 'Restoring…' : 'Restore…'}
                    </button>{' '}
                    <button
                      type="button"
                      className="btn-ghost"
                      onClick={() => setCheckModal(c)}
                      disabled={keyringLost}
                      title={keyringLostHint}
                    >
                      Check / Repair…
                    </button>{' '}
                    {/* Delete is not on this row: it lives inside Edit (see the form's form-actions
                        below). With five buttons in a row, the irreversible one sits right next to the
                        most-used Backup/Restore, and its position drifts once a narrow screen wraps them.
                        Going through the edit form both adds distance and guarantees you see which
                        configuration you are deleting. */}
                    <button type="button" className="btn-ghost" onClick={() => startEdit(c)}>
                      Edit
                    </button>
                  </td>
                </tr>
                {ops.length > 0 && (
                  <tr className="ops-row">
                    <td colSpan={6}>{ops}</td>
                  </tr>
                )}
                {/* The edit form expands under the row it belongs to rather than at the bottom of the page: on a
                    phone, where every row is a card, a form appended below the table lands several screens from its
                    own row with nothing on screen connecting the two. Same shape as the ops-row above, and it goes
                    after it — a backup that is running and being edited shows status first, then the form. A new
                    backup belongs to no row, so it keeps its place under the table. */}
                {editing?.id === c.id && (
                  <tr className="edit-row">
                    <td colSpan={6}>{formPanel}</td>
                  </tr>
                )}
                </Fragment>
                )
              })
            )}
          </tbody>
        </table>
      </div>

      {!editing && formPanel}

      {browsing && (
        <PathBrowser
          initialPath={form.localRoot || undefined}
          onPick={(p) => {
            set('localRoot', p)
            setBrowsing(false)
          }}
          onClose={() => setBrowsing(false)}
        />
      )}
      {changingRoot && editing && (
        <ChangeLocalRootDialog
          configId={editing.id}
          currentRoot={form.localRoot}
          onClose={() => setChangingRoot(false)}
          onDone={(newRoot) => {
            setChangingRoot(false)
            // load() only refreshes the list and never touches the edit form currently open — without
            // these two lines, "Local Root (locked)" keeps showing the old path until the form is closed
            // and reopened.
            setEditing((e) => (e ? { ...e, localRoot: newRoot } : e))
            set('localRoot', newRoot)
            load()
          }}
        />
      )}
      {checkModal && (
        <CheckModal
          config={checkModal}
          onClose={() => setCheckModal(null)}
          onError={setError}
        />
      )}
      {restoreModal && (
        <RestoreDialog
          config={restoreModal}
          onClose={() => setRestoreModal(null)}
          onError={setError}
          onStarted={(state) => {
            const id = restoreModal.id
            setRestoreModal(null)
            void pollRestore(id, state)
          }}
        />
      )}
      {errorModal && <ErrorModal config={errorModal} onClose={() => setErrorModal(null)} />}
      {deleteModal && (
        <DeleteModal
          config={deleteModal}
          onClose={() => setDeleteModal(null)}
          onConfirm={(deleteContainer) => remove(deleteModal, deleteContainer)}
        />
      )}
      {postCreate && (
        <PostCreateModal
          config={postCreate}
          onRunNow={() => {
            setPostCreate(null)
            void run(postCreate)
          }}
          onNotNow={() => setPostCreate(null)}
        />
      )}
      {resettingPassword && (
        <ResetPasswordModal
          config={resettingPassword}
          busy={busy}
          error={error}
          onSubmit={submitResetPassword}
          onClose={closeResetPassword}
        />
      )}
      {stopping && (
        <StopBackupDialog
          name={stopping.name}
          onStop={async (finishCurrentFiles) => {
            setError(null)
            // Marked here rather than on the button: Stop opens a confirmation dialog first, and an operator who
            // backs out of it must not be left looking at a row that claims to be stopping.
            setWindingDown((m) => ({ ...m, [stopping.id]: 'stop' }))
            try {
              await backupConfigsApi.cancel(stopping.id, 'backup', finishCurrentFiles)
              load()
            } catch (e) {
              setError(e instanceof Error ? e.message : String(e))
            }
          }}
          onClose={() => setStopping(null)}
        />
      )}
    </section>
  )
}

// The stop button, shown only while running. Stopping is asynchronous (after the signal, the run winds
// down at the next cancellation checkpoint), so pressing it does not change this row immediately — the
// wording therefore promises nothing about "stopped". Restore, repair and check still use this one (they
// have no Suspend or Retry now); backup uses RunButtons below.
function StopButton({ onStop }: { onStop: () => void }) {
  return (
    <>
      {' '}
      <button type="button" className="btn-ghost btn-danger" style={{ padding: '0 0.3rem' }} onClick={onStop}>
        Stop
      </button>
    </>
  )
}

/**
 * The case-insensitive half of a rule list, shown under its sensitive sibling.
 *
 * Two boxes rather than one, because the two halves genuinely want different matching and no rule shape reliably
 * says which: `*.mp4` names a kind of file and every casing of it is that kind, while `Temp/` names a directory
 * that on Linux is not `temp/`. Guessing from the pattern would be wrong in both directions.
 *
 * Empty is the common case and costs nothing, so the box is always shown rather than hidden behind a toggle —
 * a hidden setting is one nobody discovers until the day they wonder why `*.mp4` missed every `.MP4`.
 */
function CaseInsensitiveHalf({ value, onChange }: { value: string | null; onChange: (v: string) => void }) {
  return (
    <div style={{ marginTop: 'var(--sp-2)' }}>
      <p className="text-muted" style={{ margin: '0 0 var(--sp-1)' }}>
        Ignoring case — <span className="mono">*.mp4</span> here also matches{' '}
        <span className="mono">.MP4</span> and <span className="mono">.Mp4</span>. Same syntax as the box above;
        either box takes paths or extensions.
      </p>
      <RuleBox value={value} onChange={onChange} />
    </div>
  )
}

// The running button group, backup only (it adds Suspend and Retry now, which restore, repair and check
// have no concept of).
// Stopping is asynchronous (after the signal, the run winds down at the next cancellation checkpoint), so
// pressing it does not change this row immediately — the wording promises nothing about "stopped".
function RunButtons({
  onStop,
  onSuspend,
  onRetryNow,
  stopping,
}: {
  onStop: () => void
  onSuspend: () => void
  onRetryNow?: () => void
  stopping?: 'suspend' | 'stop'
}) {
  // Both buttons go disabled once either has been pressed: the run is winding down and asking it to wind down
  // a second, different way only produces a race nobody can predict the outcome of.
  const pending = stopping !== undefined
  return (
    <>
      {onRetryNow && (
        <>
          {' '}
          <button type="button" className="btn-ghost" style={{ padding: '0 0.3rem' }} onClick={onRetryNow}>
            Retry now
          </button>
        </>
      )}{' '}
      {/* btn-outline, not a bare btn-ghost: next to Stop's red border a borderless Suspend does not read as a
          button at all, and it is the *safe* one of the pair — the one a hesitant operator should find first. */}
      <button
        type="button"
        className="btn-ghost btn-outline"
        style={{ padding: '0 0.3rem' }}
        onClick={onSuspend}
        disabled={pending}
      >
        {stopping === 'suspend' ? 'Suspending…' : 'Suspend'}
      </button>{' '}
      <button
        type="button"
        className="btn-ghost btn-danger"
        style={{ padding: '0 0.3rem' }}
        onClick={onStop}
        disabled={pending}
      >
        {stopping === 'stop' ? 'Stopping…' : 'Stop'}
      </button>
    </>
  )
}

// An interrupted run: the round before the restart did not finish and its journal is still on disk.
function InterruptedNotice({
  runs,
  onResume,
  onDiscard,
}: {
  runs: InterruptedRun[]
  onResume: () => void
  onDiscard: () => void
}) {
  // resumable holds only when the journal belongs to this configuration and the local root has not
  // changed. This list is keyed by (accountId, containerName) rather than config id, so a journal left by
  // **another** configuration on the same container is listed too — and there resumable is false: an
  // actual run voids it rather than continuing it. So "the uploaded blocks will be reused" is true only
  // of the resumable ones, and the block count must not be summed across all runs.
  const resumable = runs.filter((r) => r.resumable)
  const blocks = resumable.reduce((n, r) => n + r.blocks, 0)
  return (
    <div className="text-warn">
      {resumable.length > 0 ? (
        <>Interrupted run — {blocks.toLocaleString()} block(s) already uploaded are kept and will be reused</>
      ) : (
        <>Interrupted run — cannot be picked up (it belongs to a different backup sharing this container)</>
      )}
      {' '}
      <button
        type="button"
        className="btn-ghost"
        style={{ padding: '0 0.3rem' }}
        onClick={onResume}
        disabled={resumable.length === 0}
        title={resumable.length === 0 ? 'Not resumable — only Discard applies' : undefined}
      >
        Resume
      </button>{' '}
      <button type="button" className="btn-ghost btn-danger" style={{ padding: '0 0.3rem' }} onClick={onDiscard}>
        Discard
      </button>
    </div>
  )
}

function RunStatus({
  run,
  onStop,
  onSuspend,
  onRetryNow,
  onResume,
  onDiscard,
  stopping,
}: {
  run: BackupRun
  onStop: () => void
  onSuspend: () => void
  onRetryNow: () => void
  onResume: () => void
  onDiscard: () => void
  /// Set once this run has been asked to wind down, so the buttons can say so while it does.
  stopping?: 'suspend' | 'stop'
}) {
  // The expanded state stays inside the component: polling replaces props every second, but React keeps the same instance, so expansion is not reset.
  const [showDetail, setShowDetail] = useState(false)

  if (run.status === 'Failed')
    return <div className="text-danger">Failed: {run.error}</div>
  // Stopping is neither success nor failure: the backend does not write it as the backup's Error status, and this does not colour it red.
  if (run.status === 'Canceled')
    return <div className="text-warn">Backup stopped — nothing was recorded for this run</div>
  // Suspension is the same, and goes further than stopping: the scene is preserved and the next run picks
  // up from here, so the button says Resume rather than Run.
  // "Resume" actually calls run() — resuming is not a mode, since every run recognises any still-valid
  // journal when it opens one. There is no separate resume endpoint; the label says Resume only because
  // that is what it means to the user.
  if (run.status === 'Suspended')
    return (
      <div className="text-warn">
        {run.suspendReason === 'AutoSuspended'
          ? 'Suspended after repeated network errors — progress is saved'
          : 'Suspended — progress is saved'}
        {' '}
        <button type="button" className="btn-ghost" style={{ padding: '0 0.3rem' }} onClick={onResume}>
          Resume
        </button>{' '}
        <button type="button" className="btn-ghost btn-danger" style={{ padding: '0 0.3rem' }} onClick={onDiscard}>
          Discard
        </button>
      </div>
    )
  if (run.status === 'Completed') {
    // What the round actually did. It was already going to the log and the notification and nowhere
    // else, so the one screen the operator is watching when a backup ends was the only place that could
    // not answer "and what did it do?" — see runTotals for the wording rules.
    const totals = runTotals(run)
    return (
      <div className="text-ok">
        {/* Its own line, not appended to the one above: that one says which version and how long, this
            one says what moved. Run together they make a sentence long enough that neither gets read. */}
        <div>
          Completed — version {run.version}
          {/* The start and end come from the version record, the same pair the restore dialog reads. An older backend does not send them → show the number only. */}
          {run.completedAt && ` (${formatVersionSpan(run.startedAt, run.completedAt)})`}
          {/* A "successful" backup may have skipped files — without saying so here, the operator can only find out from a notification that may well be drowned out. */}
          {!!run.unreadableFiles && (
            <span className="text-danger">
              {' '}— {run.unreadableFiles} file(s) could not be read; earlier content kept
            </span>
          )}
        </div>
        {totals && <div>{totals}</div>}
      </div>
    )
  }
  const p = run.progress
  if (!p)
    return (
      <div className="text-faint">
        Starting…
        <RunButtons onStop={onStop} onSuspend={onSuspend} onRetryNow={run.pause ? onRetryNow : undefined} stopping={stopping} />
      </div>
    )

  // Once pipelined, Diffing and Uploading run at the same time, so there can be two details. An older
  // backend sends only detail, so both shapes must be accepted.
  const details = p.details?.length ? p.details : p.detail ? [p.detail] : []
  const diffing = details.find((d) => d.stage === 'Diffing')
  const uploading = details.find((d) => d.stage === 'Uploading')

  // The headline progress. A single stage uses its own percentage: run.percent measures the proportion of
  // uploaded items and is always 0 during scanning and diffing — copying it would produce the
  // contradiction of 0% on top and 3% in the detail below.
  //
  // With two running in parallel, **one number will not do**. Only Diffing can produce a percentage (its
  // denominator is the entry count from the scan, settled from the start); the upload's denominator keeps
  // growing as the diff enqueues work, so it has no reliable percentage. The headline used to show
  // Diffing's number, justified as "that is what decides how much longer the round takes" — which inverts
  // exactly when it matters most: the diff decides orders of magnitude faster than compression and upload,
  // so once it is held by the bounded queue it sits still while uploading is in fact progressing, and the
  // headline looks hung.
  //
  // So the percentage is labelled as the diff's, alongside the upload's **absolute** completed volume —
  // with no reliable denominator, an absolute figure is more honest than a fake percentage that falls back.
  // Completion is by **source bytes**, not item count. An item-count percentage during upload means very
  // little: one item may be a 6.8 GB single file or a pack of several hundred 5 KB files, and counting
  // them equally is what produced a measured 75% by count against 31% by source bytes — the headline runs
  // high all the way, then the last few large items pin it at 99% for a long time. "How many items are
  // left" is stated directly by the detail line's fraction (2,003 / 2,661 objects), which is clearer than
  // folding it into a percentage.
  // It falls back to the count only when bytes are unavailable (scanning and diffing report no byte
  // workload, and during upload the denominator grows until the diff finishes).
  const singlePercent =
    (details[0]?.workPercent ?? details[0]?.percent) ??
    (p.stage >= BackupStage.Uploading ? p.percent : null)
  // Speed and remaining time come from the **upload** detail, falling back to the headline only when there is no upload.
  //
  // With both running, the diff's must not be used: the diff's ETA says "how much longer until this round
  // finishes deciding", and after that an entire upload remains — putting that on top promises a figure
  // necessarily far below the real remainder. The upload's is self-consistent: its denominator
  // (totalItems) is only settled by SetTotal once the diff finishes, and before that the backend's Eta()
  // returns null outright (no denominator, so do not guess), which is why this field is naturally empty
  // while both run — showing nothing beats showing a number that goes backwards.
  const pace = uploading ?? details[0]
  // The speed field uses the same gate as the detail line: show it whenever a transfer is in flight, even
  // if this instant reads 0 (a stream just started, nothing booked yet), or the number flickers at the
  // start and end of every stream.
  const speed =
    pace && (pace.bytesPerSecond > 0 || pace.activeItems.length > 0)
      ? `${formatBytes(pace.bytesPerSecond)}/s`
      : null
  // Computed from the seconds rather than slicing the estimatedRemaining string — see formatDuration for why.
  const eta = pace?.etaSeconds != null ? `~${formatDuration(pace.etaSeconds)} left` : null

  const headline = [
    diffing && uploading
      ? diffing.percent != null && `${diffing.percent}% diffed`
      : singlePercent != null && `${singlePercent}%`,
    // The upload's absolute volume must appear in **both** layouts. It used to hang off the parallel
    // branch only, so the moment the diff finished and the detail went from two lines back to one, the
    // tens of gigabytes already uploaded vanished from the headline — leaving a source-byte percentage
    // that sits at 0% for a long time early in a large backup, which reads as the whole round starting
    // over. The absolute figure is the one number here that never goes backwards.
    uploading && uploading.workDone > 0 && `${formatBytes(uploading.workDone)} uploaded`,
    speed,
    eta,
  ]
    .filter(Boolean)
    .join(' · ')
  // The changed count only exists once the diff has run; writing "(0 changed)" before that states something not yet true.
  const changed = p.stage >= BackupStage.Uploading ? ` (${p.changedFiles} changed)` : ''

  // Once pipelined, the diff and the upload run **at the same time**, while the backend's stage only
  // switches to Uploading when the diff finishes. Copying it would leave the headline reading "Diffing"
  // for most of the round — a first backup's diff reads every file to hash it and can run for hours, with
  // uploading progressing throughout and nothing on screen showing it. When both details are moving, say so.
  const label = details.length > 1 ? 'Diffing + Uploading' : backupStageLabels[p.stage]

  return (
    <div className="text-faint">
      {label}
      {headline && ` ${headline}`}
      {changed}
      {run.pause && (
        <div className="text-warn">
          Paused — {run.pause.reason} (attempt {run.pause.failures})
          {run.pause.nextRetryAt && `; retrying ${formatRetryIn(run.pause.nextRetryAt)}`}
        </div>
      )}
      <RunButtons onStop={onStop} onSuspend={onSuspend} onRetryNow={run.pause ? onRetryNow : undefined} stopping={stopping} />
      {/* Details are folded into an expandable area: the path being processed can be very long and would
          distort the table if laid out in the row. One line of overall progress by default, expanded when
          wanted. */}
      {details.length > 0 && (
        <>
          {' '}
          <button
            type="button"
            className="btn-ghost"
            style={{ padding: '0 0.3rem' }}
            onClick={() => setShowDetail((v) => !v)}
          >
            {showDetail ? '▾ Details' : '▸ Details'}
          </button>
          {/* Two parallel details stack vertically rather than spreading horizontally — details widening
              the table has been raised before, and a second one warrants more care. */}
          {showDetail && details.map((d) => <StageDetail key={d.stage} detail={d} />)}
        </>
      )}
    </div>
  )
}

/// Stage detail. Before this, scanning and diffing each reported once on entry — and a first backup's
/// diff reads every file end to end to hash it, which can run for hours while the UI shows a motionless
/// 0%, indistinguishable from a hang.

function StageDetail({ detail }: { detail: StageProgress }) {
  const { counts, done, pipeline, speed, eta, inFlightPhrase } = stageLines(detail)

  return (
    <div style={{ marginTop: '0.15rem', lineHeight: 1.5 }}>
      {detail.currentItem && (
        <div className="mono" style={{ wordBreak: 'break-all' }}>
          {detail.currentItem}
        </div>
      )}
      {/* Each transfer in flight gets its own line with its size and progress. This used to be crammed
          with the content-addressed blob name (an HMAC when encrypted), which showed neither which file
          was transferring nor how much of it. */}
      {/* The heading says "in parallel" explicitly: listing two or three filenames at once suggests
          parallel compression, whereas compression is globally serialised (one lock — that N preparing
          above) and it is the transfers that run in parallel. */}
      {detail.activeItems.length > 0 && (
        <div className="text-faint">
          {`${inFlightPhrase} in parallel:`}
        </div>
      )}
      {/* All of them are listed, no longer truncated at three. The in-flight count is bounded — it is the
          upload/download concurrency from settings (5 by default), and the gate issues slots per
          **volume**, so it does not grow with queue length or file size. Folding a few into "+2 more"
          hides the most useful thing: the stuck one is usually among those folded away. */}
      {detail.activeItems.map((a) => (
        <div key={a.label} className="mono" style={{ wordBreak: 'break-all' }}>
          {a.label}
          {a.total > 0 && (
            <span className="text-faint">
              {' '}
              — {formatBytes(a.sent)} / {formatBytes(a.total)}
              {a.percent !== null && ` · ${a.percent}%`}
            </span>
          )}
        </div>
      ))}
      <div>
        {/* The stage name has to be spelled out: with two details side by side, two rows of numbers alone do not say which is the diff and which is the upload. */}
        <span className="text-faint">{detail.stage}: </span>
        {counts}
        {/* The item-count percentage appears only when bytes cannot provide one (scanning and diffing
            report no byte workload) — and there it shares a basis with the fraction beside it. The
            byte-based completion follows its own fraction. */}
        {detail.workPercent == null && detail.percent != null && ` · ${detail.percent}%`}
        {done && ` · ${done}`}
        {speed}
        {eta}
      </div>
      {/* The second line is the whole pipeline, ordered backwards along the timeline. The prefix states
          that none of it has settled — without it, "1.9 TB uploaded" on the first line and "+3.7 GB on
          the cloud" on the second become a puzzle again: both are bytes already in the cloud, so why two
          lines? Because the first belongs to items that have settled and will not change, while the
          second belongs to items still running, whose volumes stop counting if the item starts over. */}
      {pipeline && <div className="text-faint">In flight: {pipeline}</div>}
    </div>
  )
}

// The status badge (§4.2, decision 2): in-progress (blue, from the derived activity) outranks a persistent Error (red, with tooltip and Reset); otherwise nothing is shown.
function StatusBadge({
  config, onReset, onShowError,
}: { config: BackupConfig; onReset: () => void; onShowError: () => void }) {
  if (config.activity !== 'Idle') {
    return <span className="badge badge-info">{activityBadgeLabels[config.activity]}</span>
  }
  if (config.status === BackupStatus.Error) {
    return (
      <span className="row-inline">
        {/* The badge opens the full text. The error used to live only in the title attribute: a wall of
            Azure exception text is unreadable in a tooltip, and nobody thinks to hover — so after a page
            refresh it became "go find the error in the logs". The text was persisted all along
            (BackupConfig.LastError); all it lacked was somewhere readable to put it. */}
        <button type="button" className="badge badge-danger" onClick={onShowError}>
          Error
        </button>
        <button type="button" className="btn-ghost" onClick={onReset}>
          Reset
        </button>
      </span>
    )
  }
  return <span className="text-faint">—</span>
}

/// The full text of the backup's last failure. Azure exceptions are long and carry XML, so this needs room, scrolling and copying.
function ErrorModal({ config, onClose }: { config: BackupConfig; onClose: () => void }) {
  const [copied, setCopied] = useState(false)
  const text = config.lastError ?? 'No error detail was recorded.'

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
    } catch {
      // The clipboard was blocked by browser policy (not https, or no permission) — the text is right below and can still be selected manually.
    }
  }

  return (
    <Modal
      title={`Last error — ${config.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={copy}>
            {copied ? 'Copied' : 'Copy'}
          </button>
          <button type="button" onClick={onClose}>
            Close
          </button>
        </>
      }
    >
      {config.lastErrorAt && (
        <p className="text-faint" style={{ marginTop: 0 }}>
          {new Date(config.lastErrorAt).toLocaleString()}
        </p>
      )}
      <pre
        className="mono"
        style={{
          maxHeight: '50vh', overflow: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-all',
          background: 'var(--bg-raised)', border: '1px solid var(--border)',
          borderRadius: 'var(--r-md)', padding: 'var(--sp-3)', margin: 0,
        }}
      >
        {text}
      </pre>
    </Modal>
  )
}

// The same three-state display as RunStatus; RepairRun has no version or progress field (see api/backupConfigs.ts), so neither is shown.
function RepairStatus({ run, onStop }: { run: RepairRun; onStop: () => void }) {
  if (run.status === 'Failed')
    return <div className="text-danger">Repair failed: {run.error}</div>
  if (run.status === 'Canceled')
    return <div className="text-warn">Repair stopped — files already repaired are kept</div>
  if (run.status === 'Completed')
    return <div className="text-ok">Repair completed</div>
  return <div className="text-faint">Repairing…<StopButton onStop={onStop} /></div>
}

// A check's run state. The report itself is read in the Check/Repair dialog — this row only answers "is
// it still running, and how far" — because a content-level check downloads and re-hashes the entire
// backup and can run for hours.
function CheckStatus({ run, onStop }: { run: CheckRun; onStop: () => void }) {
  const [showDetail, setShowDetail] = useState(false)

  if (run.status === 'Failed')
    return <div className="text-danger">Check failed: {run.error}</div>
  if (run.status === 'Canceled')
    return <div className="text-warn">Check stopped — no report was produced</div>
  if (run.status === 'Completed') {
    const r = run.report
    if (!r) return <div className="text-ok">Check completed</div>
    return (
      <div className={r.ok ? 'text-ok' : 'text-danger'}>
        {r.ok
          ? `Check completed — all checked objects OK (version ${r.version})`
          : `Check completed — ${r.missingRefs.length} problem(s), ${r.repairablePaths.length} repairable (version ${r.version})`}
      </div>
    )
  }

  return (
    <div className="text-faint">
      Checking
      {run.detail?.percent != null && ` ${run.detail.percent}%`}
      <StopButton onStop={onStop} />
      {run.detail && (
        <>
          {' '}
          <button
            type="button"
            className="btn-ghost"
            style={{ padding: '0 0.3rem' }}
            onClick={() => setShowDetail((v) => !v)}
          >
            {showDetail ? '▾ Details' : '▸ Details'}
          </button>
          {showDetail && <StageDetail detail={run.detail} />}
        </>
      )}
    </div>
  )
}

function RestoreStatus({ run, onStop }: { run: RestoreRun; onStop: () => void }) {
  const [showDetail, setShowDetail] = useState(false)
  // A record per skipped or failed file. It matters most after completion — a single number cannot say which files, or why.
  const events = run.events ?? []
  const toggle = events.length > 0 || run.detail ? (
    <>
      {' '}
      <button
        type="button"
        className="btn-ghost"
        style={{ padding: '0 0.3rem' }}
        onClick={() => setShowDetail((v) => !v)}
      >
        {showDetail ? '▾ Details' : '▸ Details'}
      </button>
    </>
  ) : null

  const detailBlock = showDetail && (
    <>
      {run.detail && <StageDetail detail={run.detail} />}
      {events.length > 0 && (
        <ul className="mono" style={{ margin: '0.2rem 0 0 1.2rem', wordBreak: 'break-all' }}>
          {events.map((e, i) => (
            <li key={i}>{e}</li>
          ))}
        </ul>
      )}
    </>
  )

  if (run.status === 'Failed')
    return (
      <div className="text-danger">
        Restore failed: {run.error}
        {toggle}
        {detailBlock}
      </div>
    )
  if (run.status === 'Completed')
    return (
      <div className={run.failedFiles ? 'text-warn' : 'text-ok'}>
        Restored {run.restoredFiles} file(s), skipped {run.skippedFiles}
        {run.failedFiles ? `, failed ${run.failedFiles}` : ''} — version {run.version}
        {toggle}
        {detailBlock}
      </div>
    )
  // A restore writes file by file and there is no such thing as a rollback: whatever already landed stays in the target directory after stopping.
  if (run.status === 'Canceled')
    return (
      <div className="text-warn">
        Restore stopped — files already written are kept
        {toggle}
        {detailBlock}
      </div>
    )
  return (
    <div className="text-faint">
      {run.phase || 'Restoring…'}
      <StopButton onStop={onStop} />
      {toggle}
      {detailBlock}
    </div>
  )
}

function TierSelect({
  value,
  onChange,
  archive,
  disabled,
}: {
  value: number
  onChange: (v: number) => void
  archive: boolean
  disabled?: boolean
}) {
  return (
    <select value={value} disabled={disabled} onChange={(e) => onChange(Number(e.target.value))}>
      <option value={StorageTier.Hot}>{tierLabels[StorageTier.Hot]}</option>
      <option value={StorageTier.Cool}>{tierLabels[StorageTier.Cool]}</option>
      <option value={StorageTier.Cold}>{tierLabels[StorageTier.Cold]}</option>
      {archive && <option value={StorageTier.Archive}>{tierLabels[StorageTier.Archive]}</option>}
    </select>
  )
}

function RuleBox({ value, onChange }: { value: string | null; onChange: (v: string) => void }) {
  return (
    <textarea
      rows={2}
      placeholder="gitignore syntax, one per line"
      className="w-lg"
      value={value ?? ''}
      onChange={(e) => onChange(e.target.value)}
    />
  )
}

// Delete confirmation (§4.3): by default only the local configuration, cache and logs are removed and the
// cloud container is kept. Ticking deleteContainer adds a second window.confirm stressing that it is
// irreversible, so a whole container is not deleted by mistake.
function DeleteModal({
  config, onClose, onConfirm,
}: {
  config: BackupConfig
  onClose: () => void
  /** Throwing counts as failure and the error is shown inside this dialog. On success the caller closes it. */
  onConfirm: (deleteContainer: boolean) => Promise<void>
}) {
  const [deleteContainer, setDeleteContainer] = useState(false)
  // The reason for failure must be shown **inside the dialog**. It used to go to the page's global error,
  // which the dialog covers — so when the backend refused to delete a running configuration (409), what
  // the user saw was "I pressed Delete and nothing happened".
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const confirm = async () => {
    if (deleteContainer) {
      const sure = window.confirm(
        `This will PERMANENTLY delete the Azure container "${config.containerName}" and ALL backup data in it. ` +
          'This cannot be undone. Are you absolutely sure?',
      )
      if (!sure) return
    }
    setError(null)
    setBusy(true)
    try {
      await onConfirm(deleteContainer)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      title={`Delete Backup — ${config.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-danger" onClick={confirm} disabled={busy}>
            {busy ? 'Deleting…' : 'Delete'}
          </button>
          <button type="button" onClick={onClose}>
            Cancel
          </button>
        </>
      }
    >
      <p>This removes the local backup configuration, cached index, and logs.</p>
      <label style={{ display: 'flex', gap: '0.5rem', alignItems: 'flex-start', margin: '0.8rem 0' }}>
        <input
          type="checkbox"
          checked={deleteContainer}
          onChange={(e) => setDeleteContainer(e.target.checked)}
        />
        <span className={deleteContainer ? 'text-danger' : undefined}>
          Also delete cloud container (irreversible — erases all backup data)
        </span>
      </label>
      {error && (
        <div className="text-danger" style={{ margin: '0.8rem 0' }}>
          {error}
        </div>
      )}
    </Modal>
  )
}

// §4.6: after a configuration is created, offer to run the first backup now. "Run now" reuses the same
// run-and-poll logic as the table row (progress appears in that configuration's row; there is no separate
// progress page).
function PostCreateModal({
  config, onRunNow, onNotNow,
}: { config: BackupConfig; onRunNow: () => void; onNotNow: () => void }) {
  return (
    <Modal
      title={`Backup Created — ${config.name}`}
      onClose={onNotNow}
      footer={
        <>
          <button type="button" className="btn-primary" onClick={onRunNow}>
            Run first backup now
          </button>
          <button type="button" onClick={onNotNow}>
            Not now
          </button>
        </>
      }
    >
      <p>Run the first backup now?</p>
    </Modal>
  )
}

// The keyring-loss recovery dialog: re-enter the original backup password. The password itself cannot be
// changed — it can only be verified, and it is persisted only once verification succeeds (decrypting the
// cloud info file). A failure returns 400 carrying "Verification failed: …", shown as-is.
function ResetPasswordModal({
  config, busy, error, onSubmit, onClose,
}: {
  config: BackupConfig
  busy: boolean
  error: string | null
  onSubmit: (password: string) => void
  onClose: () => void
}) {
  const [password, setPassword] = useState('')

  return (
    <Modal
      title={`Re-enter Password — ${config.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-primary" onClick={() => onSubmit(password)} disabled={busy || !password}>
            Submit
          </button>
          <button type="button" onClick={onClose} disabled={busy}>
            Cancel
          </button>
        </>
      }
    >
      <p>
        Enter the original password used to encrypt this backup. It cannot be changed — a
        different password will fail verification.
      </p>

      <Field label="Password">
        <input
          type="password"
          className="w-lg"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
      </Field>

      {error && <p className="text-danger">{error}</p>}
    </Modal>
  )
}

function CheckModal({
  config, onClose, onError,
}: { config: BackupConfig; onClose: () => void; onError: (e: string) => void }) {
  // As in the restore dialog: a number cannot identify "the one from last Thursday", so the whole record is kept to show the start and end times.
  const [versions, setVersions] = useState<BackupVersionInfo[]>([])
  const [version, setVersion] = useState<number | null>(null)
  const [cloud, setCloud] = useState<number>(CloudCheckLevel.ExistenceSize)
  const [local, setLocal] = useState<number>(LocalCheckLevel.Content)
  const [rehydrate, setRehydrate] = useState<number | null>(null)
  const [listOrphans, setListOrphans] = useState(false)
  const [running, setRunning] = useState(false)
  const [checkRun, setCheckRun] = useState<CheckRun | null>(null)
  const [repairing, setRepairing] = useState(false)
  const [repairReport, setRepairReport] = useState<RepairRun | null>(null)
  // Polling has to stop when the dialog closes, or it keeps writing state into an unmounted component.
  const aliveRef = useRef(true)
  useEffect(() => () => { aliveRef.current = false }, [])

  const report = checkRun?.report ?? null

  // Check is a background job now: the POST only returns 202, and both the result and the progress come from polling.
  const follow = async (initial: CheckRun) => {
    setRunning(true)
    try {
      let run = initial
      setCheckRun(run)
      while (run.status === 'Running') {
        await delay(1000)
        if (!aliveRef.current) return null
        // A running check always has state to report; if it really comes back empty, stop polling rather than blanking the run.
        const next = await backupConfigsApi.checkStatus(config.id)
        if (!next) break
        run = next
        setCheckRun(run)
      }
      if (run.status === 'Failed' && run.error) onError(run.error)
      return run
    } finally {
      if (aliveRef.current) setRunning(false)
    }
  }

  // A ref rather than putting follow in the effect's dependencies: follow is a new function every render,
  // and depending on it directly would make this "read once on open" effect re-run every render.
  const followRef = useRef(follow)
  followRef.current = follow

  useEffect(() => {
    backupConfigsApi.versions(config.id).then(setVersions).catch(() => {})
    // The server keeps the most recent check report: closing and reopening the dialog must bring the
    // result back, and re-running a content-level check downloads and re-hashes the entire backup at real
    // egress cost. Empty = never checked.
    // Still running means keep polling; already finished means just display the report — not through
    // follow, so that a previous failure is not raised again as if it were this run's error.
    backupConfigsApi
      .checkStatus(config.id)
      .then((s) => { if (!s) return; if (s.status === 'Running') void followRef.current(s); else setCheckRun(s) })
      .catch(() => {})
  }, [config.id])

  const rehydrateArg = () => (cloud === CloudCheckLevel.Content ? rehydrate : null)

  // Check is a background job and its progress is already in the table row (Checking N% plus Details), so
  // duplicating it here means nothing: on a successful start the dialog just closes, and the report is
  // read back from the server the next time it opens.
  const runCheck = async () => {
    setRepairReport(null)
    try {
      await backupConfigsApi.check(config.id, cloud, local, version, rehydrateArg(), listOrphans)
      onClose()
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e))
    }
  }

  const stopCheck = async () => {
    try {
      await backupConfigsApi.cancel(config.id, 'check')
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e))
    }
  }

  const runRepair = async () => {
    setRepairing(true)
    try {
      // Repair is a background job (holding the lock until it completes); poll for its state.
      let run = await backupConfigsApi.repair(config.id, cloud, version, rehydrateArg(), listOrphans)
      setRepairReport(run)
      while (run.status === 'Running') {
        await delay(1500)
        run = await backupConfigsApi.repairStatus(config.id)
        setRepairReport(run)
      }
      if (run.status === 'Completed')
        await follow(await backupConfigsApi.check(config.id, cloud, local, version, rehydrateArg(), listOrphans))
      else if (run.error) onError(run.error)
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e))
    } finally {
      setRepairing(false)
    }
  }

  const problems = report ? report.findings.filter((f) => f.cloud === CloudState.MissingOrBad) : []
  // Entries whose content was carried over from an earlier version: the cloud blob itself is usually fine, so they are not in problems, but they still have to be reported.
  const stale = report ? report.findings.filter((f) => f.unreadableAt) : []

  return (
    <Modal
      title={`Check / Repair — ${config.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-primary" onClick={runCheck} disabled={running || repairing}>
            {running ? 'Checking…' : 'Run check'}
          </button>
          {running && (
            <button type="button" className="btn-danger" onClick={stopCheck}>Stop</button>
          )}
          {(problems.some((f) => f.repairable) || (report?.orphanBlobs?.length ?? 0) > 0) && (
            <button type="button" onClick={runRepair} disabled={repairing || running}>
              {repairing ? 'Repairing…' : 'Repair from local'}
            </button>
          )}
          <button type="button" onClick={onClose}>Close</button>
        </>
      }
    >
      <Field label="Version">
        <select value={version ?? ''} onChange={(e) => setVersion(e.target.value === '' ? null : Number(e.target.value))}>
          <option value="">Latest</option>
          {versions.map((v) => (
            <option key={v.version} value={v.version}>
              Version {v.version} — {formatVersionSpan(v.startedAt, v.createdAt)}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Cloud check">
        <select value={cloud} onChange={(e) => setCloud(Number(e.target.value))}>
          {Object.entries(cloudLevelLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
      </Field>
      <Field label="Local check">
        <select value={local} onChange={(e) => setLocal(Number(e.target.value))}>
          {Object.entries(localLevelLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
      </Field>
      {cloud === CloudCheckLevel.Content && (
        <Field label="Rehydrate Archive to">
          <select value={rehydrate ?? ''} onChange={(e) => setRehydrate(e.target.value === '' ? null : Number(e.target.value))}>
            <option value="">Don't rehydrate</option>
            <option value={StorageTier.Hot}>Hot</option>
            <option value={StorageTier.Cool}>Cool</option>
          </select>
        </Field>
      )}
      <Field label="Unreferenced blobs">
        {/* The explanatory text and the checkbox share the outer <label class="field"> rather than
            nesting another <label>: labels cannot nest, and the nested one let the checkbox escape
            .field's centring rule, leaving it sitting too high. */}
        <span className="field-check">
          <input type="checkbox" checked={listOrphans} onChange={(e) => setListOrphans(e.target.checked)} />
          Detect unreferenced blobs (repair deletes them)
        </span>
      </Field>

      {/* Progress is not repeated here: the check runs in the background on the server and the table row
          already has the stage, the percentage and Details. The dialog only states that it is running (the
          report exists only once it finishes) and offers Stop. */}
      {running && (
        <div className="text-faint" style={{ marginBottom: '0.6rem' }}>
          A check is running — progress is shown in the backup list. The report appears here when it finishes.
        </div>
      )}
      {checkRun?.status === 'Canceled' && (
        <div className="text-warn" style={{ marginBottom: '0.6rem' }}>Check stopped — no report was produced.</div>
      )}
      {checkRun?.status === 'Failed' && (
        <div className="text-danger" style={{ marginBottom: '0.6rem' }}>Check failed: {checkRun.error}</div>
      )}

      {repairReport && (
        <div style={{ marginBottom: '0.6rem' }}>
          {repairReport.status === 'Running' && 'Repairing (backup is locked until done)…'}
          {repairReport.status === 'Failed' && <span className="text-danger">Repair failed: {repairReport.error}</span>}
          {repairReport.status === 'Completed' && (
            <>
              Repaired {repairReport.repaired?.length ?? 0} file(s);{' '}
              <span className={repairReport.unrecoverable?.length ? 'text-danger' : undefined}>
                {repairReport.unrecoverable?.length ?? 0} unrecoverable
              </span>
              {(repairReport.unrecoverable?.length ?? 0) > 0 && `: ${repairReport.unrecoverable!.join(', ')}`}
              {(repairReport.deletedOrphans?.length ?? 0) > 0 &&
                `; deleted ${repairReport.deletedOrphans!.length} unreferenced blob(s)`}
            </>
          )}
        </div>
      )}

      {report && (
        <div>
          {report.metadataIssue && (
            <div className="text-danger">Metadata drift: {report.metadataIssue}</div>
          )}
          <div className={report.ok ? 'text-ok' : 'text-danger'} style={{ margin: '0.4rem 0' }}>
            {report.ok ? 'All checked objects OK' : `${problems.length} problem(s), ${report.repairablePaths.length} repairable from local`}
            {' '}(version {report.version})
          </div>
          {listOrphans && (
            <div className={report.orphanBlobs.length ? 'text-warn' : 'text-ok'} style={{ margin: '0.4rem 0' }}>
              {report.orphanBlobs.length === 0
                ? 'No unreferenced blobs found'
                : `${report.orphanBlobs.length} unreferenced blob(s) — repair will delete: ${report.orphanBlobs.slice(0, 20).join(', ')}${report.orphanBlobs.length > 20 ? '…' : ''}`}
            </div>
          )}
          {/* A carried-over entry's cloud blob is usually fine (cloud=Ok), so they never appear in the
              problem table below — but the operator has to know this version contains stale content,
              especially since it makes the local comparison read as Changed. */}
          {stale.length > 0 && (
            <div className="text-warn" style={{ margin: '0.4rem 0' }}>
              {stale.length} file(s) hold content carried forward from an earlier backup — the
              source could not be read since then, so a local comparison shows them as changed:
              <ul style={{ margin: '0.2rem 0 0 1.2rem' }}>
                {stale.slice(0, 20).map((f) => (
                  <li key={f.path}>
                    <span className="mono">{f.path}</span>
                    {' '}— unread since {new Date(f.unreadableAt!).toLocaleString()}
                  </li>
                ))}
              </ul>
              {stale.length > 20 && <div>…and {stale.length - 20} more</div>}
            </div>
          )}
          {problems.length > 0 && (
            <div className="table-scroll" tabIndex={0}>
              <table className="text-faint">
                <thead><tr><th>File</th><th>Cloud</th><th>Local</th><th>Repairable</th></tr></thead>
                <tbody>
                  {problems.map((f) => (
                    <tr key={f.path}>
                      <td className="mono">
                        {f.path}
                        {f.unreadableAt && <span className="text-warn"> (carried forward)</span>}
                      </td>
                      <td className="text-danger" style={{ textAlign: 'center' }}>{cloudStateLabel(f.cloud)}</td>
                      <td style={{ textAlign: 'center' }}>{localStateLabel(f.local)}</td>
                      <td style={{ textAlign: 'center' }}>{f.repairable ? 'yes' : 'no'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </Modal>
  )
}
