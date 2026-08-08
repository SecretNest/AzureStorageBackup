using System.Net.Sockets;
using Azure;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 瞬时（可重试、可挂起）错误的唯一判据。上传重试与挂起闸门共用一套，
/// 免得两边各判各的，出现"重试层认为该重试、闸门层认为该失败"这种自相矛盾。
/// </summary>
public static class TransientErrors
{
    /// <param name="ct">
    /// 调用方的取消令牌。取消是唯一需要上下文才能分辨的情况：同样是
    /// <see cref="OperationCanceledException"/>，令牌已触发说明是**用户按了取消**（必须往上抛），
    /// 没触发说明是 SDK 内部的网络超时（该重试）。判错这一条，取消按钮会静悄悄失效。
    /// </param>
    public static bool IsTransient(Exception ex, CancellationToken ct = default) => ex switch
    {
        RequestFailedException rfe => rfe.Status == 0 || rfe.Status >= 500 || rfe.Status is 408 or 429,
        IOException => true,
        SocketException => true,
        TimeoutException => true,
        OperationCanceledException => !ct.IsCancellationRequested,
        // Azure.Core 重试耗尽时抛的就是这个（内层一串 TaskCanceledException）。
        // 从前这里漏判，导致我们自己那层 RetryPolicy 一次都没重试，直接把运行判死。
        AggregateException agg => agg.InnerExceptions.Count > 0
            && agg.InnerExceptions.All(inner => IsTransient(inner, ct)),
        _ => false,
    };
}
