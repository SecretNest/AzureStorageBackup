import { useKeyringStatus } from '../api/keyring'

/**
 * The persistent banner shown while the key ring is lost (design §3.5).
 * Accounts must be recovered first — verifying a backup password needs the cloud, and reaching
 * the cloud needs the account key.
 *
 * The status comes from the shared store rather than a fetch of its own: after a successful reset,
 * each page calls refreshKeyringStatus() and the banner disappears with it. Fetching once in
 * useEffect(…, []) would leave the warning up after recovery until a hard refresh.
 */
export function KeyringBanner({ onGoToAccounts }: { onGoToAccounts: () => void }) {
  const status = useKeyringStatus()

  if (!status || status.status === 'Healthy') return null

  const pending = status.accountsPending + status.backupConfigsPending

  return (
    <div role="alert" className="alert alert-warn">
      <strong>Data protection keys were lost</strong> — {pending} credential
      {pending === 1 ? '' : 's'} need to be re-entered before backups can run.
      {status.accountsPending > 0 && (
        <>
          {' '}
          Start with{' '}
          {/* Clicking goes to Settings — Accounts is now the top section of that page, so the button
              has to say "in Settings", or the click lands somewhere unexpected. */}
          <button type="button" className="btn-ghost" onClick={onGoToAccounts}>
            Accounts in Settings
          </button>
          {' '}({status.accountsPending} pending), then re-enter backup passwords
          ({status.backupConfigsPending} pending).
        </>
      )}
    </div>
  )
}
