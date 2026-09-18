using System;
using System.Globalization;
using System.Windows.Data;
using NetDisk.App.Localization;

namespace NetDisk.App.Converters;

/// <summary>ShareItem.revoked(bool) → 状态文案。</summary>
public sealed class RevokedToStatus : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Loc.T("Share.StatusRevoked") : Loc.T("Share.StatusValid");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>ShareItem.max_downloads(long?) → "不限" 或数字(0/空视为不限)。</summary>
public sealed class MaxDownloadsText : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return Loc.T("Share.Unlimited");
        if (value is long l && l <= 0) return Loc.T("Share.Unlimited");
        return value.ToString()!;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
