using Microsoft.UI.Xaml.Data;
using EF.UI.Client.Notifications;

namespace TaskFlow.Uno.Converters;

/// <summary>Converts severity to info bar severity values for Uno XAML binding and display.</summary>
public sealed class SeverityToInfoBarSeverityConverter : IValueConverter
{
    /// <summary>Converts values for XAML binding or display logic.</summary>
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is NotificationSeverity severity
            ? severity switch
            {
                NotificationSeverity.Success => InfoBarSeverity.Success,
                NotificationSeverity.Warning => InfoBarSeverity.Warning,
                NotificationSeverity.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            }
            : InfoBarSeverity.Informational;

    /// <summary>Converts values back from UI bindings when reverse conversion is supported.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
