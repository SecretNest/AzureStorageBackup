using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// 测试程序集需要直接驱动 SchedulerService.TickAsync（internal）以覆盖密钥环跳过分支，
// 不希望仅为测试把该方法开成 public。
[assembly: InternalsVisibleTo("AzureStorageBackup.Api.Tests")]

// 这个程序集只在 Linux 上跑，这是**事实陈述**而不是压制警告：交付形态是
// mcr.microsoft.com/dotnet/aspnet:10.0（Debian）容器，还依赖官方 7zz 二进制与 Unix 权限位，
// 换个平台从一开始就跑不起来。
// 不声明的话，Unix 权限那几个 API（File.Get/SetUnixFileMode，用于给读不开的文件补读权限）
// 会各自报一条 CA1416「在 windows 上不受支持」，整个解决方案七十来条。噪音本身是有代价的：
// 真冒出一条新警告会被埋在里面看不见——这在本项目的几轮审查里已经险些发生过。
// 顺带说明一处：SevenZipCompressor.Grant 的 catch 只列了 IOException 与 UnauthorizedAccessException，
// 没接 PlatformNotSupportedException。在 Windows 上那会炸穿——而这行声明正是说"不存在那个前提"。
[assembly: SupportedOSPlatform("linux")]
