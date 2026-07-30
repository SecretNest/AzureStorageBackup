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
    <div role="alert" className="alert alert-warn">
      <strong>Data protection keys were lost</strong> — {pending} credential
      {pending === 1 ? '' : 's'} need to be re-entered before backups can run.
      {status.accountsPending > 0 && (
        <>
          {' '}
          Start with{' '}
          {/* 点了会跳到 Settings——Accounts 现在是那一页最上面的一个区域，所以按钮得把
              "在 Settings 里"说出来，否则点完会有一瞬"我点的是 Accounts 怎么到 Settings 了"。 */}
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
