using System;
using OmenCore.Models;

namespace OmenCore.ViewModels
{
    public static class TuningStatusFormatter
    {
        /// <summary>
        /// AMD Curve Optimizer offsets go to the SMU as whole counts of 4 mV-equivalent, truncated toward
        /// zero (AmdUndervoltProvider), so -15 is applied as -12. A difference under one step is the
        /// rounding, not a failed write (GitHub #222).
        /// </summary>
        public const double AmdCoStepMv = 4.0;

        /// <summary>True when the applied offset matches the request, allowing for AMD's step rounding.</summary>
        public static bool UndervoltOffsetMatches(double appliedMv, double requestedMv, bool isAmdCpu) =>
            Math.Abs(appliedMv - requestedMv) <= 0.5 ||
            (isAmdCpu && Math.Abs(appliedMv - requestedMv) < AmdCoStepMv && Math.Abs(appliedMv) <= Math.Abs(requestedMv));

        private static bool UndervoltOffsetIsExact(double appliedMv, double requestedMv) =>
            Math.Abs(appliedMv - requestedMv) <= 0.5;

        public static string BuildUndervoltStatusText(double requestedCoreMv, double requestedCacheMv, UndervoltStatus? status, bool isAmdCpu)
        {
            if (status == null)
            {
                return global::OmenCore.Localization.Strings.CsRequestedNAAppliedNAVerified;
            }

            var requested = isAmdCpu
                ? string.Format(global::OmenCore.Localization.Strings.CsRequestedCoreMVEqIGPUMVEq, requestedCoreMv, requestedCacheMv)
                : string.Format(global::OmenCore.Localization.Strings.CsRequestedCoreMVCacheMV, requestedCoreMv, requestedCacheMv);

            var applied = isAmdCpu
                ? string.Format(global::OmenCore.Localization.Strings.CsAppliedCoreMVEqIGPUMVEq, status.CurrentCoreOffsetMv, status.CurrentCacheOffsetMv)
                : string.Format(global::OmenCore.Localization.Strings.CsAppliedCoreMVCacheMV, status.CurrentCoreOffsetMv, status.CurrentCacheOffsetMv);

            string verified;
            if (status.HasExternalController)
            {
                verified = string.Format(global::OmenCore.Localization.Strings.CsVerifiedBlockedByExternalController, status.ExternalController);
            }
            else if (!string.IsNullOrWhiteSpace(status.Error))
            {
                verified = string.Format(global::OmenCore.Localization.Strings.CsVerifiedFailed, status.Error);
            }
            else if (!string.IsNullOrWhiteSpace(status.Warning))
            {
                verified = string.Format(global::OmenCore.Localization.Strings.CsVerifiedWarning, status.Warning);
            }
            else
            {
                var coreMatch = UndervoltOffsetMatches(status.CurrentCoreOffsetMv, requestedCoreMv, isAmdCpu);
                var cacheMatch = UndervoltOffsetMatches(status.CurrentCacheOffsetMv, requestedCacheMv, isAmdCpu);
                var rounded = !UndervoltOffsetIsExact(status.CurrentCoreOffsetMv, requestedCoreMv) ||
                              !UndervoltOffsetIsExact(status.CurrentCacheOffsetMv, requestedCacheMv);
                verified = coreMatch && cacheMatch
                    ? (rounded
                        ? string.Format(global::OmenCore.Localization.Strings.CsVerifiedAppliedAtTheNearestCurveOptimizer, AmdCoStepMv)
                        : status.HasIndependentReadback
                        ? global::OmenCore.Localization.Strings.CsVerifiedReadbackMatchesRequested
                        : global::OmenCore.Localization.Strings.CsVerifiedWriteAcknowledgedNoIndependentHardwareReadback)
                    : status.ControlledByOmenCore
                        ? global::OmenCore.Localization.Strings.CsVerifiedMismatchBetweenRequestedAndReadback
                        : global::OmenCore.Localization.Strings.CsVerifiedNotControlledByOmenCore;
            }

            return $"{requested} | {applied} | {verified}";
        }

        public static string BuildGpuOcStatusText(
            int requestedCore,
            int requestedMemory,
            int requestedPower,
            int requestedVoltage,
            int appliedCore,
            int appliedMemory,
            int appliedPower,
            int appliedVoltage,
            bool backendAvailable,
            bool allWritesSucceeded)
        {
            if (!backendAvailable)
            {
                return global::OmenCore.Localization.Strings.CsRequestedNAAppliedNAVerified_fd20c;
            }

            var requested = string.Format(global::OmenCore.Localization.Strings.CsRequestedCoreMemPowerVoltage, FormatSigned(requestedCore, "MHz"), FormatSigned(requestedMemory, "MHz"), requestedPower, FormatSigned(requestedVoltage, "mV"));
            var applied = string.Format(global::OmenCore.Localization.Strings.CsAppliedCoreMemPowerVoltage, FormatSigned(appliedCore, "MHz"), FormatSigned(appliedMemory, "MHz"), appliedPower, FormatSigned(appliedVoltage, "mV"));

            string verified;
            if (!allWritesSucceeded)
            {
                verified = global::OmenCore.Localization.Strings.CsVerifiedOneOrMoreWritesFailed;
            }
            else if (requestedCore == appliedCore && requestedMemory == appliedMemory && requestedPower == appliedPower && requestedVoltage == appliedVoltage)
            {
                verified = global::OmenCore.Localization.Strings.CsVerifiedReadbackMatchesRequested;
            }
            else
            {
                verified = global::OmenCore.Localization.Strings.CsVerifiedMismatchBetweenRequestedAndReadback;
            }

            return $"{requested} | {applied} | {verified}";
        }

        private static string FormatSigned(int value, string unit)
        {
            return value >= 0 ? $"+{value} {unit}" : $"{value} {unit}";
        }
    }
}
