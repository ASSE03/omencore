using System;
using System.Globalization;
using System.Windows.Data;
using OmenCore.Models;

namespace OmenCore.Utils
{
    /// <summary>Shows a performance mode by the profile card's name ("Balanced", not the firmware's "Default").</summary>
    public sealed class ModeDisplayNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            Localize(PerformanceModeNameResolver.ToDisplayName(value as string));

        /// <summary>
        /// Translates the three built-in mode names ToDisplayName returns; user-named modes are shown
        /// as typed. Display only: the canonical English names stay the values the app compares.
        /// </summary>
        public static string Localize(string displayName) => displayName switch
        {
            "Balanced" => global::OmenCore.Localization.Strings.CsModeBalanced,
            "Quiet" => global::OmenCore.Localization.Strings.CsModeQuiet,
            "Performance" => global::OmenCore.Localization.Strings.CsModePerformance,
            _ => displayName
        };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
