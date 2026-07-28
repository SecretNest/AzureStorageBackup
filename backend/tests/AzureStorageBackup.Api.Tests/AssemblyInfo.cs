using System.Runtime.Versioning;

// 与被测程序集保持同一个平台前提（见 src 侧 AssemblyInfo 的说明）。测试里的
// chmod 000 装置大量使用 File.SetUnixFileMode 去造"读不开的文件"，不声明的话
// 光这个测试项目就是六十多条 CA1416。
[assembly: SupportedOSPlatform("linux")]
