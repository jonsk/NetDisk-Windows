// 开机自启(DE-D-18):HKCU\...\Run 的登记/取消。
//
// 三条纪律:
//
//  ①**只写 HKCU**(当前用户),不碰 HKLM:写 HKLM 需要管理员权限,而"开机自启"是**用户**
//    的选择(7.7 的装机形态是 perUser,见 R-26)。要求提权才能自启的客户端,用户会直接放弃。
//
//  ②**路径必须加引号**:`C:\Program Files\NetDisk\NetDisk.App.exe` 不加引号的话,Windows
//    会把它解析成"执行 C:\Program" + 参数 —— 用户重启后**什么都不会发生**,而且没有任何
//    报错(这是自启类功能最经典的失败形态)。
//
//  ③**取消要幂等**:用户可能反复开关;删一个不存在的值不该报错(否则设置页会弹出莫名的失败)。
//
// 子键名可注入是为了**可测**:检查器在 `Software\NetDiskTest\<随机>` 下做真实的注册表
// 往返(创建/读回/删除),不必污染真正的开机启动项。

using Microsoft.Win32;

namespace NetDisk.SyncEngine.Notify;

/// <summary>开机自启的登记接口(便于测试与"不写注册表"的场景)。</summary>
public interface IStartupRegistration
{
    /// <summary>当前是否已登记(登记项指向的路径是否就是 <paramref name="exePath"/> 的当前形态)。</summary>
    bool IsEnabled(string exePath);

    /// <summary>登记开机自启(幂等:重复调用覆盖为最新路径)。</summary>
    void Enable(string exePath);

    /// <summary>取消(幂等:不存在也不报错)。</summary>
    void Disable();
}

/// <summary>注册表实现(perUser)。</summary>
public sealed class RegistryStartupRegistration : IStartupRegistration
{
    /// <summary>默认值名(与产品名一致,便于用户在"任务管理器→启动"里认出)。</summary>
    public const string DefaultValueName = "NetDisk";

    private readonly string _subKey;
    private readonly string _valueName;

    public RegistryStartupRegistration(
        string valueName = DefaultValueName,
        string subKey = @"Software\Microsoft\Windows\CurrentVersion\Run")
    {
        _valueName = valueName;
        _subKey = subKey;
    }

    public string SubKey => _subKey;

    public string ValueName => _valueName;

    public bool IsEnabled(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: false);
        var current = key?.GetValue(_valueName) as string;
        return current is not null && current == CommandFor(exePath);
    }

    public void Enable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("可执行文件路径不能为空", nameof(exePath));
        }
        using var key = Registry.CurrentUser.CreateSubKey(_subKey, writable: true)
                        ?? throw new InvalidOperationException("无法打开注册表键:" + _subKey);
        key.SetValue(_valueName, CommandFor(exePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        // 幂等:键或值不存在都当作"已经是取消状态"
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 命令行形态:路径**必须加引号**(见文件头纪律②);路径两侧的空白去掉。
    /// </summary>
    public static string CommandFor(string exePath) => "\"" + exePath.Trim().Trim('"') + "\"";
}
