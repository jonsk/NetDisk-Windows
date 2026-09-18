// ============================================================================
// 本地化服务(桌面端多语言 P0,2026-09-18)。
//
// 职责:持当前语言、按 Culture 查资源、**免重启即时切换**、回退默认。
// 即时切换的原理:
//   · 实现 INotifyPropertyChanged;切换语言时对**所有键抬一次 PropertyChanged
//     (传空字符串表示"全部")—— WPF 里这会让绑定到本实例 Indexer 的所有控件一并刷新。
//   · XAML 侧统一用 `{Binding [Key], Source={x:Static Loc:Loc.Instance}}`,
//     绑定走实例的 `this[string key]` 索引器 → 切语言即时更新,无需重启。
//
// 零新增依赖:底层就 .NET 的 CultureInfo + 字典,不引 WPFLocalizeExtension(守项目纪律)。
// ============================================================================

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace NetDisk.App.Localization;

/// <summary>
/// 本地化服务实例:供 XAML `{Binding [Key], Source={x:Static}}` 与 C# `Loc.*` 统一使用。
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private string _code = Locale.DefaultCode;

    /// <summary>当前语言 code(zh/en)。设为新值会触发全局刷新。</summary>
    public string Code
    {
        get => _code;
        set
        {
            var c = Normalize(value);
            if (string.Equals(c, _code, StringComparison.Ordinal))
            {
                return;
            }
            _code = c;
            CultureInfo.CurrentUICulture = new CultureInfo(c == "en" ? "en" : Locale.DefaultCulture.Name);
            // 传空字符串 = 通知"所有属性都变了",让绑定到本实例索引器的控件全部刷新
            OnPropertyChanged("");
        }
    }

    /// <summary>当前语言对应的 CultureInfo(供日期/数字格式化)。</summary>
    public CultureInfo Culture => Code == "en" ? new CultureInfo("en") : Locale.DefaultCulture;

    /// <summary>索引器:供 XAML `{Binding [Key], ...}`。</summary>
    public string this[string key] => Get(key);

    /// <summary>按 key 取当前语言文本;缺失回退默认语言,再缺失回退 key 本身(便于发现漏键)。</summary>
    public string Get(string key)
    {
        var table = Code == "en" ? Locale.TableFor("en") : Locale.TableFor("zh");
        if (table.TryGetValue(key, out var v))
        {
            return v;
        }
        if (Code != Locale.DefaultCode && Locale.TableFor("zh").TryGetValue(key, out var dv))
        {
            return dv;
        }
        return key;
    }

    /// <summary>带占位符的本地化文本(防止手工 + 拼接导致中英文语序错)。</summary>
    public string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(Culture, template, args);
        }
        catch (FormatException)
        {
            return template; // 占位符不匹配时宁可显示原文,也不崩
        }
    }

    /// <summary>把任意输入归一化为受支持的 code;未知一律回退默认。</summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Locale.DefaultCode;
        }
        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }
        return Locale.DefaultCode; // zh / 其它一律中文
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>本地化静态入口:App 装配时初始化,界面与业务统一走这儿。</summary>
public static class Loc
{
    private static readonly LocalizationService _instance = new();

    /// <summary>全局实例(供 XAML `Source={x:Static Loc:Loc.Instance}`)。</summary>
    public static LocalizationService Instance => _instance;

    /// <summary>初始化语言(启动时根据 client.json 调用);空/未知值归一化回默认。返回实例便于装配。</summary>
    public static LocalizationService Init(string? code)
    {
        _instance.Code = LocalizationService.Normalize(code);
        return _instance;
    }

    /// <summary>取当前语言文本(等价 Instance.Get)。</summary>
    public static string T(string key) => _instance.Get(key);

    /// <summary>带占位符的本地化文本。</summary>
    public static string F(string key, params object?[] args) => _instance.Format(key, args);

    /// <summary>当前语言 code。</summary>
    public static string Current => _instance.Code;
}
