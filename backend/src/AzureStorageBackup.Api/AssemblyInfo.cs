using System.Runtime.CompilerServices;

// 测试程序集需要直接驱动 SchedulerService.TickAsync（internal）以覆盖密钥环跳过分支，
// 不希望仅为测试把该方法开成 public。
[assembly: InternalsVisibleTo("AzureStorageBackup.Api.Tests")]
