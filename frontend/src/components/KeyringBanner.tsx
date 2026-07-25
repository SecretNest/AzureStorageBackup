import { useKeyringStatus } from '../api/keyring'

/**
 * 密钥环丢失时的常驻横幅(设计 §3.5)。文案一律英文。
 * 账户必须先恢复——验证备份密码需要连云，连云需要账户密钥。
 *
 * 状态取自共享 store 而非自己拉一次：重设成功后各页面会 refreshKeyringStatus()，
 * 横幅随之消失。自己在 useEffect(…, []) 里拉一次的话，恢复完成后警告会一直挂到硬刷新。
 */
export function KeyringBanner({ onGoToAccounts }: { onGoToAccounts: () => void }) {
  const status = useKeyringStatus()

  if (!status || status.status === 'Healthy') return null

  const pending = status.accountsPending + status.backupConfigsPending

  return (
    <div
      role="alert"
      style={{
        border: '1px solid #b45309',
        background: '#fffbeb',
        color: '#7c2d12',
        padding: '0.75rem 1rem',
        borderRadius: 6,
        marginBottom: '1rem',
      }}
    >
      <strong>Data protection keys were lost</strong> — {pending} credential
      {pending === 1 ? '' : 's'} need to be re-entered before backups can run.
      {status.accountsPending > 0 && (
        <>
          {' '}
          Start with{' '}
          <button type="button" onClick={onGoToAccounts}>
            Accounts
          </button>
          {' '}({status.accountsPending} pending), then re-enter backup passwords
          ({status.backupConfigsPending} pending).
        </>
      )}
    </div>
  )
}
