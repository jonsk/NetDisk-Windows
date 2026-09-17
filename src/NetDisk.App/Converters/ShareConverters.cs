using System;
using System.Globalization;
using System.Windows.Data;

namespace NetDisk.App.Converters;

/// <summary>ShareItem.revoked(bool) → 状态文案。</summary>
public sealed class RevokedToStatus : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "已吊销" : "有效";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>ShareItem.max_downloads(long?) → "不限" 或数字(0/空视为不限)。</summary>
public sealed class MaxDownloadsText : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return "不限";
        if (value is long l && l <= 0) return "不限";
        return value.ToString()!;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
