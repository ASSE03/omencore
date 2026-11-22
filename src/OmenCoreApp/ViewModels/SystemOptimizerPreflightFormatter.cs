using System.Linq;
using OmenCore.Services.SystemOptimizer;

namespace OmenCore.ViewModels
{
    public static class SystemOptimizerPreflightFormatter
    {
        public static string BuildSummary(PreflightReport report)
        {
            var rebootTag = report.RequiresReboot ? global::OmenCore.Localization.Strings.CsRebootYes : global::OmenCore.Localization.Strings.CsRebootNo;
            return string.Format(global::OmenCore.Localization.Strings.CsPreflightLowMediumHigh, report.LowRiskCount, report.MediumRiskCount, report.HighRiskCount, rebootTag);
        }

        public static string BuildWarningRollup(PreflightReport report, int maxWarnings = 2)
        {
            if (report.Warnings.Count == 0)
            {
                return global::OmenCore.Localization.Strings.CsNoAdditionalPreflightWarnings;
            }

            var selected = report.Warnings.Take(maxWarnings).ToList();
            var suffix = report.Warnings.Count > maxWarnings ? string.Format(global::OmenCore.Localization.Strings.CsMore, report.Warnings.Count - maxWarnings) : string.Empty;
            return string.Join(" | ", selected) + suffix;
        }
    }
}
