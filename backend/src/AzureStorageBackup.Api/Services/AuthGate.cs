using System.Security.Cryptography;
using System.Text;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 预置密码判定（设计 §2、§4.3）。密码来自环境变量明文，**不经 Data Protection**——
/// 因此密钥环丢失时仍能登录，进而走密钥环恢复流程（设计 §5）。
/// 单例：构造时读一次配置，之后不再变（改密码需改环境变量并重启）。
/// </summary>
public sealed class AuthGate
{
    private readonly byte[]? _expected;

    public AuthGate(IConfiguration config)
    {
        var password = config["Auth:Password"];
        _expected = string.IsNullOrEmpty(password) ? null : Encoding.UTF8.GetBytes(password);
    }

    /// <summary>是否启用认证。未配置密码时为 false，全部放行。</summary>
    public bool Required => _expected is not null;

    /// <summary>
    /// 校验密码。未启用认证时恒为 true。
    /// 用恒定时间比较防时序侧信道；长度不同直接失败（长度差异本就无法隐藏）。
    /// </summary>
    public bool Verify(string? candidate)
    {
        if (_expected is null)
            return true;
        if (string.IsNullOrEmpty(candidate))
            return false;

        var actual = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(_expected, actual);
    }
}
