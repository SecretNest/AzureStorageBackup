import type { StageProgress } from '../api/backupConfigs'
import { formatBytes, formatDuration } from '../constants/format'

const STAGE_UNITS: Record<string, string> = {
  Scanning: 'entries',
  Diffing: 'files',
  Uploading: 'objects',
  Restoring: 'objects',
  // 检查的各阶段。Cloud 数的是存储对象（一个 pack 一次 HEAD），Verifying 数的是下载解压
  // 重算 hash 的包，Local 数的才是索引里的文件条目——三者数量差着数量级，不能共用一个词。
  Cloud: 'objects',
  Verifying: 'objects',
  Local: 'files',
  Orphans: 'blobs',
}

/**
 * 给一个数配上它自己的单位词，单复数跟着数走。
 *
 * 在途那一行里同时躺着两种口径的数——上传按**卷**登记（VolumeBlobIO 每卷一条，上传闸门也按卷
 * 排队），其余按**件**。从前只给卷那两项写了单位，件那几项光秃秃地摆着，一行读下来像是
 * 「1 volume uploading · 1 preparing」这样时有时无，反而更像笔误而不是刻意区分。现在每个数
 * 一律自报单位：读的人不必记住哪一项是哪种口径，也不会再拿这一行的数去凑总数。
 */
function withUnit(n: number, plural: string): string {
  const singular = plural === 'entries' ? 'entry' : plural.replace(/s$/, '')
  return `${n.toLocaleString()} ${n === 1 ? singular : plural}`
}

/**
 * 把一份阶段快照拆成屏幕上那两行。
 *
 * 抽出来是为了能测：这两行的全部难点在**顺序**与**措辞**，而两者都只能靠读代码确认——
 * 拼好的字符串一旦进了 JSX 就再没有地方能断言它。顺序错了不会有任何东西报错，只会让
 * 屏幕上重新出现"这两个数到底谁先谁后"那种问不完的问题。
 */
export function stageLines(detail: StageProgress) {
  const unit = STAGE_UNITS[detail.stage] ?? 'items'
  // 用 "of" 而不是 "/"：斜杠是分数记号，摆在那儿就是在邀人约一个百分比出来，而件数百分比在
  // 上传阶段恰恰没什么意义（一件可能是 6.8 GB 的单文件，也可能是一箱几百个 5 KB 的小文件）。
  // 真正的完成度按字节算，在下一行那个 "60.6 GB / 191.0 GB original (31%)" 上——那里用斜杠，
  // 因为它的百分比就跟在后面，约得出来也约得对。
  const counts =
    detail.total > 0
      ? `${detail.processed.toLocaleString()} of ${detail.total.toLocaleString()} ${unit}`
      : `${detail.processed.toLocaleString()} ${unit} so far` // 扫描时总数未知——它正是扫描要算出来的
  // 在途分解。光一个"处理了 N 件"看不出是在干活还是卡住了：备份的上传阶段里，一件活要先过
  // 7z（一箱 100 MB 可以压几十秒）才轮到推字节，那段时间 uploading 是 0 而 preparing 不是；
  // 还原/校验阶段同理，下载一结束就退出在途窗口，随后的解压/算 hash 那段本地 CPU 工作同样要
  // 占着 preparing 才不会从界面上消失。三个数各自有值才出现——扫描、差分这些阶段没有队列，
  // 全是 0，这一段自然整体消失。
  //
  // preparing 现在是**阶段相关**的，不只是数值范围不同、连数的是什么都不同：上传阶段数的是
  // "拿到全局压缩锁、正在产出卷文件"的活，压缩锁只有一把，永远是 0 或 1，排在它后面等锁的
  // 活算进 queued；还原/校验阶段数的是"下载完、正在解压/算 hash"的组，那里没有全局锁，
  // 最多可以有 DownloadConcurrency 个组同时在解，不是 0/1。标签跟着这层含义走。
  //
  // 一个字节都没在传、手上却有活在准备，这种时候**不能**只让 "N uploading" 悄悄消失：
  // 那一刻速度是 0、进度条不动、在途路径一行都没有，界面上看跟卡死一模一样。把原因写出来。
  // 措辞不提"压缩"：选了不压缩的备份一样要过一遍 7z（加密、打包、分卷），说 compressing 是错的；
  // 还原/校验阶段则确实是在解压/算 hash，直说就行。
  const preparingLabel = detail.stage === 'Uploading' ? 'preparing' : 'extracting'
  // 压完了、字节却还没上网线的件卡在哪一段。三段的处置完全不同，所以分开说：
  // · peer  —— 同一份内容正由别人上传，只能等它整件传完（几分钟起步）
  // · slot  —— 全局上传闸门排满了（这一项数的是**卷**，闸门按卷排队）
  // · cloud —— 在等云端应答（存在性/元数据 HEAD），网络一慢就是几十秒
  // 不分开的话，屏幕上就只剩"什么都没在传"这一句，而这正是查不下去的地方。
  //
  // 件数口径：peer 与 cloud 数的是**件**，slot 数的是**卷**。所以只有前两项参与件数加法，
  // slot 单独说，措辞里点明单位。
  //
  // 剩下的那些（uploading 减掉 peer 与 checking）是已进上传段、但字节还没上路、又说不出在等什么
  // 的件：登记完"进上传段"到第一卷起飞之间、卷与卷之间的空档、查去重映射时没排上队的那一段。
  // 读盘核对那几段（预筛整读、pack 逐成员 Stat、云端清残留）已经拆进 checking 自己那一栏，
  // 必须从这里减掉——不减就是同一件活报两遍，屏幕上的数再也凑不出
  // processed + preparing + queued + uploading 那条恒等式。
  // 只在**一条流都没在传**时才报：有流在传时上面已经有一句 "N uploading" 了，再加一档只会让人
  // 分不清哪个数是哪个。这个理由对 checking 不成立（那一栏说得清自己在干什么），所以它不设这个门。
  const stalled = Math.max(0, detail.uploading - detail.waitingOnPeer - detail.checking)
  const idleOnStaging = detail.activeItems.length === 0 && detail.preparing > 0
  const inFlightVerb = detail.stage === 'Uploading' ? 'uploading' : 'downloading'
  // 在途那个数的单位**两侧不一样**：
  // · 上传：VolumeBlobIO 每一卷各登记一条，一件大活自己就能占满全部并发额度（默认 5）；
  // · 下载：RestoreOrchestrator / BackupChecker 按整个对象（或整组）登记一条，多卷共用它。
  // 点明单位的后果是实打实的：同一行里 processed 与 queued 数的是**件**，把卷数加进去就超过
  // 总数——实测 5,346 + 5 + 1,031 = 6,382 > 6,378，多出的 4 正是「5 卷 − 1 件」。
  // "5 volumes uploading" / "1 volume uploading" / "3 objects downloading"
  const inFlightPhrase = `${withUnit(
    detail.activeItems.length,
    detail.stage === 'Uploading' ? 'volumes' : unit,
  )} ${inFlightVerb}`
  const downloading = detail.stage === 'Restoring' || detail.stage === 'Verifying'
  // 件数与字节放进**同一条时间轴**——这是这一段重排的全部意义。从前件数一行、字节一行，
  // 两行各自按自己的逻辑排，于是屏幕上没有任何东西说得出 "+2.0 GB unfinished" 与
  // "100 MB ready to upload" 是不是同一件活的两半（它们不是：前者的字节已经在云上了，
  // 后者还没上路，中间隔着整整一段上传）。这两个数真的被对着追问过很久，而答案本来
  // 只需要把它们按顺序摆开。
  //
  // 排列按**逆时间轴**：越接近"这件活干完了"的排越前，越早的阶段排越后，末尾落到 queued。
  // 一件活的正序是 queued → waiting for the archive slot（排全局产出锁）→ preparing（占锁产出）
  // → 归档落盘 → checking（重校验 / 清云端残留卷）→ ready to upload → starting upload
  // → waiting on peer/slot（等资源）→ uploading → on the cloud → 销账并进上面那一行。
  // 倒着念就是下面这个数组。
  //
  // to go 与 buffered to disk 落在末尾：前者是下载侧还没恢复的源字节，后者是 diff 的领先量
  // （活已入队、还没轮到），两者都在时间轴最靠前的那一头。
  const pipeline = [
    // 已落云、所属那件活还没销账的字节。措辞不再写 "uploaded in unfinished objects"——
    // 整行已经以 "In flight" 起头，再说一遍 unfinished 是重复；"on the cloud" 直说它在哪。
    detail.unfinishedItemBytes > 0 && `+${formatBytes(detail.unfinishedItemBytes)} on the cloud`,
    detail.activeItems.length > 0 && inFlightPhrase,
    // "right now" 而不是 "yet"：这一条说的是**这一瞬**没有流在传（手上那件正占着压缩锁），
    // 不是"还没开始过"。跑到一半时上一行已经有几个 TB 的累计量了，说 "yet" 是错的。
    idleOnStaging && 'nothing on the wire right now',
    detail.waitingOnPeer > 0 &&
      `${withUnit(detail.waitingOnPeer, unit)} waiting on the same content elsewhere`,
    // 单位是**卷**不是件（闸门按卷排队），与相邻各项刻意不同。
    detail.waitingOnSlot > 0 &&
      `${withUnit(detail.waitingOnSlot, 'volumes')} waiting for an upload slot`,
    detail.activeItems.length === 0 && stalled > 0 && `${withUnit(stalled, unit)} starting upload`,
    // 紧跟在 starting upload 后面：这些字节正是那几件活手上已经核对完、就等上路的产出。
    // "ready to upload" 这个词现在名副其实了——还卡在核对里的那部分已经拆去下面那一栏，
    // 不再混进来。从前它把两者一起报，于是"压完了"被说成了"可以传了"，而重校验判出成员
    // 变过的话，这份归档会被整个丢掉重压，一个字节都传不出去。
    detail.stagedBytes > 0 && `${formatBytes(detail.stagedBytes)} ready to upload`,
    // 读盘核对那几段（去重预筛整读、pack 压缩前后各逐成员 stat、云端清残留卷）。它们一个
    // 进度事件都不发，而心跳只在有流在传时才跑——不报出来的话屏幕上就是几分钟纹丝不动的
    // "1 object starting upload"，既没在 starting，也没在 upload。
    detail.checking > 0 && `${withUnit(detail.checking, unit)} checking files`,
    // 其中已经压完落盘、却还没资格上路的字节。归档还不存在的那几段（去重预筛整读源文件、
    // 压缩前的逐成员 stat）在这里是 0，那时池子里一个字节都没有。
    detail.checkingBytes > 0 && `${formatBytes(detail.checkingBytes)} being checked`,
    detail.preparing > 0 && `${withUnit(detail.preparing, unit)} ${preparingLabel}`,
    // 排在那把全局归档锁后面干等的。措辞刻意**不**说 compressing/compressor：这把锁保护的是
    // "产出这件活的卷文件"，而 store-only 只打包不压、raw 直传连 7z 都不过，三条路占同一把锁。
    // 判别方法免费送：preparing=1 + 这一栏非 0 = 锁在自己手里，正常排队；
    // preparing=0 + 这一栏非 0 = 锁在别的运行手里，可以去把那个停掉。
    detail.waitingOnArchive > 0 &&
      `${withUnit(detail.waitingOnArchive, unit)} waiting for the archive slot`,
    detail.queued > 0 && `${withUnit(detail.queued, unit)} queued`,
    downloading && detail.workRemaining > 0 && `${formatBytes(detail.workRemaining)} to go`,
    // 差分判得比压缩上传快几个数量级，跑到前面去是常态；多出来的活攒在磁盘上等下游消化。
    // 这一行取代了从前那句 "waiting for upload to catch up"——写侧不再阻塞了，所以要说的
    // 不再是"卡住了"，而是"领先了多少"。措辞里点明 buffered，别让人以为这是失败重试。
    detail.spilledItems > 0 && `${withUnit(detail.spilledItems, unit)} buffered to disk`,
  ]
    .filter(Boolean)
    .join(' · ')
  // 门开在"有没有在途流"上，不开在数值上：卡住的流会被心跳一路摁到 0（见 StageTracker.Tick），
  // 这正是要显示出来的信号——真卡住时应该看到 "0 B/s"，而不是这一行整段消失、让人分不清
  // 是没在传还是卡死了。Uploading/Restoring/Verifying 三个会登记在途项的阶段都是边传边报字节
  // （下载同样挂了逐卷 progress，见 VolumeBlobIO.DownloadAsync），此前只有 Uploading 是这样，
  // 现在三者对称，不必再单独把 Uploading 摘出来。其余七个阶段从不调 BeginItem，
  // activeItems 恒为空，条件退化回原来的 bytesPerSecond > 0，行为不变。
  const speed =
    detail.bytesPerSecond > 0 || detail.activeItems.length > 0
      ? ` · ${formatBytes(detail.bytesPerSecond)}/s`
      : ''
  // 从秒数算，不去切 estimatedRemaining 那个字符串——理由见 formatDuration。
  const eta = detail.etaSeconds !== null ? ` · ~${formatDuration(detail.etaSeconds)} left` : ''

  // 第一行只留**已经落定**的字节：完成度分数与真正传上去的量。在途的一切都归下面那条
  // 时间轴。两者的界线就是"这件活还会不会变卦"——上面的不会了，下面的都还可能重来。
  //
  // 上传与下载是两个方向，措辞不能共用：上传是「压缩 → 送走」，下载是「拉下来 → 写出去」，
  // 而且下载侧的总量事先就知道（索引里记着各卷尺寸），上传侧压完才知道，只能报已完成的。
  const done = (
    downloading
      ? [
          // 已下载 / 总下载：分母来自索引；老索引缺卷尺寸时后端报 0，这里就只显示分子。
          detail.transferredBytes > 0 &&
            (detail.transferTotal > 0
              ? `${formatBytes(detail.transferredBytes)} / ${formatBytes(detail.transferTotal)} downloaded`
              : `${formatBytes(detail.transferredBytes)} downloaded`),
          // 已恢复：解压后写出去的源字节。还没恢复的那部分（to go）在下面那条时间轴的末尾。
          detail.workDone > 0 && `${formatBytes(detail.workDone)} restored`,
        ]
      : [
          // 已完成 / 总量，两边都是**源端**字节（压缩前）。分数只有同口径才有意义——拿实传
          // 字节当分子是不行的：分母（压缩后的总量）在开始传之前根本不存在，压完才知道，
          // 而且压缩率随文件类型大幅摆动，跨口径的比例读不出任何东西。
          //
          // 措辞用 original / compressed 这一对，跟压缩工具里 Original Size / Compressed Size
          // 的惯例一致。原先叫 "uploaded" 是错的：它让人以为这是传上去的量，而这两个数说的是
          // **原始文件有多大**——压不动的内容两个数几乎相等，那个词就把口径彻底藏起来了。
          //
          // 完成度百分比就跟在这个分数后面——它算的正是这两个数，摆在一起谁都不会认错。
          detail.workTotal > 0 &&
            `${formatBytes(detail.workDone)} / ${formatBytes(detail.workTotal)} original${
              detail.workPercent != null ? ` (${detail.workPercent}%)` : ''
            }`,
          // 这一轮真正传出去的字节，后面跟上它占原始尺寸的比例。
          //
          // 措辞换了四轮，每一轮都指出同一件事——这个数的**口径**看不出来：
          // · 叫 "stored" 被读成"云上一共存了多少"（它和整条明细一样只说本次运行）；
          // · 与前面那个分数"有什么不同"同样看不出来，两个数都以 GB 结尾，口径却一个是原始尺寸
          //   一个是实传，而压不动的内容（媒体、已压缩文件）两者几乎相等，光摆着像重复了一遍；
          // · 叫 "on the wire" 更糟——下面那句 "nothing on the wire right now" 正用着同一个词，
          //   一处说没有、一处说 2 GB；
          // · 叫 "compressed" 会自相矛盾：不压缩(store-only)又加了密时，7z 封装加 AES 让产物
          //   **比原文件大**，小文件的归档头开销同样如此，于是出现 "compressed (105%)"。
          //
          // 落回 "uploaded"——分数改叫 original 之后这个词就空出来了，而它本来就是最准的：
          // 传出去多少就是多少，不预设变大还是变小。超过 100% 照样读得通（上传了原始尺寸的
          // 105%），而且那正是要告诉用户的事：这样配下来云上反而更占地方。
          detail.transferredBytes > 0 &&
            `${formatBytes(detail.transferredBytes)} uploaded${
              detail.workDone > 0
                // 括号里必须带上"of original"。光一个 (95%) 会被读成"上传进度 95%"——
                // 而同一行**就有**一个真正的进度百分比，两个挨着，混读几乎是必然的。
                ? ` (${Math.round((100 * detail.transferredBytes) / detail.workDone)}% of original)`
                : ''
            }`,
        ]
  )
    .filter(Boolean)
    .join(' · ')
  // 交出 inFlightPhrase 而不是 unit + inFlightVerb 两块料：在途列表的标题
  // （"5 volumes uploading in parallel:"）拼的正是同一句，让调用方再拼一遍就等于把
  // "上传按卷、下载按件"这条口径规则复制到了第二个地方，改一处漏一处。
  return { counts, done, pipeline, speed, eta, inFlightPhrase }
}
