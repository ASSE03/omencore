using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace OmenCore.Localization
{
    /// <summary>
    /// Translates text that OmenCore.Core produces for display (bloatware descriptions, removal
    /// results, status lines). Core cannot reference this assembly's catalog, so the English it
    /// emits is matched here, at the point where it reaches the UI, against the templates in
    /// <see cref="Templates"/>; anything unmatched is shown unchanged. Values captured by a
    /// template ({0}, {1}...) are translated recursively, so nested messages ("{0} Command error:
    /// {1}") and scope lists ("all users, provisioned image") come out translated too.
    ///
    /// The English stays the source of truth in Core: logs, diagnostics exports and tests still
    /// see it untouched, and a Core message that changes wording simply falls back to English.
    /// </summary>
    public static class CoreText
    {
        // English template exactly as Core produces it -> catalog key. Generated with the catalog.
        private static readonly (string Template, string Key)[] Templates =
        {
            ("Administrator privileges are required to create a restore point.", nameof(Strings.CoreAdministratorPrivilegesAreRequiredToCreateA)),
            ("Creating pre-removal system restore point...", nameof(Strings.CoreCreatingPreRemovalSystemRestorePoint)),
            ("Unknown restore point failure", nameof(Strings.CoreUnknownRestorePointFailure)),
            ("Restore point creation was canceled.", nameof(Strings.CoreRestorePointCreationWasCanceled)),
            ("Scanning for bloatware...", nameof(Strings.CoreScanningForBloatware)),
            ("Administrator privileges required. Please restart OmenCore as Administrator to remove applications.", nameof(Strings.CoreAdministratorPrivilegesRequiredPleaseRestartOmenCoreAs)),
            ("Removal was skipped (no state change required).", nameof(Strings.CoreRemovalWasSkippedNoStateChangeRequired)),
            ("Removal completed and verification confirmed the item is no longer detected.", nameof(Strings.CoreRemovalCompletedAndVerificationConfirmedTheItem)),
            ("Removal command completed but post-state verification detected the item is still present.", nameof(Strings.CoreRemovalCommandCompletedButPostStateVerification)),
            ("Package was already absent before removal attempt.", nameof(Strings.CorePackageWasAlreadyAbsentBeforeRemovalAttempt)),
            ("PowerShell AppX removal exited with non-zero code.", nameof(Strings.CorePowerShellAppXRemovalExitedWithNonZero)),
            ("Access denied — run OmenCore as Administrator to remove provisioned packages.", nameof(Strings.CoreAccessDeniedRunOmenCoreAsAdministratorTo)),
            ("Removal command completed, but AppX presence verification failed.", nameof(Strings.CoreRemovalCommandCompletedButAppXPresenceVerification)),
            ("AppX removal failed and verification could not complete.", nameof(Strings.CoreAppXRemovalFailedAndVerificationCouldNot)),
            ("AppX package removal verified across current user, all users, and provisioned scopes.", nameof(Strings.CoreAppXPackageRemovalVerifiedAcrossCurrentUser)),
            ("Could not parse Win32 uninstall command.", nameof(Strings.CoreCouldNotParseWin32UninstallCommand)),
            ("Uninstaller exited successfully but app was still detected after verification.", nameof(Strings.CoreUninstallerExitedSuccessfullyButAppWasStill)),
            ("Win32 app uninstall verified; registry no longer reports the app as installed.", nameof(Strings.CoreWin32AppUninstallVerifiedRegistryNoLonger)),
            ("Startup file moved out of the startup folder successfully.", nameof(Strings.CoreStartupFileMovedOutOfTheStartup)),
            ("Startup registry key could not be opened for removal.", nameof(Strings.CoreStartupRegistryKeyCouldNotBeOpened)),
            ("Startup registry entry removed successfully.", nameof(Strings.CoreStartupRegistryEntryRemovedSuccessfully)),
            ("Scheduled task disabled successfully.", nameof(Strings.CoreScheduledTaskDisabledSuccessfully)),
            ("Restore canceled before completion.", nameof(Strings.CoreRestoreCanceledBeforeCompletion)),
            ("Skipped because bulk restore was canceled before this item was processed.", nameof(Strings.CoreSkippedBecauseBulkRestoreWasCanceledBefore)),
            ("current user", nameof(Strings.CoreCurrentUser)),
            ("all users", nameof(Strings.CoreAllUsers)),
            ("provisioned image", nameof(Strings.CoreProvisionedImage)),
            ("HP security software - can conflict with third-party AV", nameof(Strings.CoreHPSecuritySoftwareCanConflictWithThird)),
            ("HP diagnostics/telemetry tool - safe to remove", nameof(Strings.CoreHPDiagnosticsTelemetryToolSafeToRemove)),
            ("HP OMEN software - conflict-sensitive until OmenCore fan, RGB, and hotkey control are verified", nameof(Strings.CoreHPOMENSoftwareConflictSensitiveUntilOmenCore)),
            ("OMEN lighting/control component - remove only after OmenCore keyboard lighting and OMEN key behavior are verified", nameof(Strings.CoreOMENLightingControlComponentRemoveOnlyAfter)),
            ("HP audio enhancement software", nameof(Strings.CoreHPAudioEnhancementSoftware)),
            ("HP system control software - OmenCore replaces this", nameof(Strings.CoreHPSystemControlSoftwareOmenCoreReplacesThis)),
            ("Trial antivirus - can impact gaming performance", nameof(Strings.CoreTrialAntivirusCanImpactGamingPerformance)),
            ("Xbox app - keep if you use Xbox Game Pass", nameof(Strings.CoreXboxAppKeepIfYouUseXbox)),
            ("Microsoft video editor", nameof(Strings.CoreMicrosoftVideoEditor)),
            ("Microsoft Bing news/weather app", nameof(Strings.CoreMicrosoftBingNewsWeatherApp)),
            ("Windows tips and help app", nameof(Strings.CoreWindowsTipsAndHelpApp)),
            ("Microsoft Solitaire with ads", nameof(Strings.CoreMicrosoftSolitaireWithAds)),
            ("Groove Music / Movies & TV app", nameof(Strings.CoreGrooveMusicMoviesTVApp)),
            ("Windows People app", nameof(Strings.CoreWindowsPeopleApp)),
            ("Skype messaging app", nameof(Strings.CoreSkypeMessagingApp)),
            ("Phone Link app", nameof(Strings.CorePhoneLinkApp)),
            ("3D/Mixed Reality app - rarely used", nameof(Strings.Core3DMixedRealityAppRarelyUsed)),
            ("Promotional app shortcut", nameof(Strings.CorePromotionalAppShortcut)),
            ("Pre-installed casual game", nameof(Strings.CorePreInstalledCasualGame)),
            ("Social media app", nameof(Strings.CoreSocialMediaApp)),
            ("Dolby audio enhancement - may affect audio quality", nameof(Strings.CoreDolbyAudioEnhancementMayAffectAudioQuality)),
            ("OneDrive startup - disable if not using cloud sync", nameof(Strings.CoreOneDriveStartupDisableIfNotUsingCloud)),
            ("Cortana assistant startup", nameof(Strings.CoreCortanaAssistantStartup)),
            ("Microsoft Teams startup - disable if not using", nameof(Strings.CoreMicrosoftTeamsStartupDisableIfNotUsing)),
            ("HP software startup item", nameof(Strings.CoreHPSoftwareStartupItem)),
            ("App startup - disable if not needed immediately", nameof(Strings.CoreAppStartupDisableIfNotNeededImmediately)),
            ("HP scheduled task", nameof(Strings.CoreHPScheduledTask)),
            ("Telemetry data collection task", nameof(Strings.CoreTelemetryDataCollectionTask)),
            ("OneDrive scheduled sync task", nameof(Strings.CoreOneDriveScheduledSyncTask)),
            ("Scheduled Task", nameof(Strings.CoreScheduledTask)),
            ("Startup Folder", nameof(Strings.CoreStartupFolder)),
            ("Startup Item", nameof(Strings.CoreStartupItem)),
            ("No HP/OMEN conflict-sensitive control components selected.", nameof(Strings.CoreNoHPOMENConflictSensitiveControlComponents)),
            ("Fan control verified in OmenCore on this hardware.", nameof(Strings.CoreFanControlVerifiedInOmenCoreOnThis)),
            ("Keyboard lighting/RGB behavior verified in OmenCore on this hardware.", nameof(Strings.CoreKeyboardLightingRGBBehaviorVerifiedInOmenCore)),
            ("OMEN key and HP Fn/media hotkeys verified after OmenCore hotkey handling.", nameof(Strings.CoreOMENKeyAndHPFnMediaHotkeys)),
            ("BIOS/driver/update path understood before removing HP support utilities.", nameof(Strings.CoreBIOSDriverUpdatePathUnderstoodBeforeRemoving)),
            ("Windows restore point planned before removal.", nameof(Strings.CoreWindowsRestorePointPlannedBeforeRemoval)),
            ("Conflict-sensitive hardware control component; verify OmenCore fan, RGB, and hotkey behavior first.", nameof(Strings.CoreConflictSensitiveHardwareControlComponentVerifyOmenCore)),
            ("High-risk removal candidate.", nameof(Strings.CoreHighRiskRemovalCandidate)),
            ("Medium-risk removal candidate; review before removal.", nameof(Strings.CoreMediumRiskRemovalCandidateReviewBeforeRemoval)),
            ("Low-risk removal candidate.", nameof(Strings.CoreLowRiskRemovalCandidate)),
            ("No risk reason available.", nameof(Strings.CoreNoRiskReasonAvailable)),
            ("Restorable by OmenCore when source state is still available.", nameof(Strings.CoreRestorableByOmenCoreWhenSourceStateIs)),
            ("No direct OmenCore restore; rely on restore point, installer, or Windows repair path.", nameof(Strings.CoreNoDirectOmenCoreRestoreRelyOnRestore)),
            ("Restore package for the current user/provisioning source if still available.", nameof(Strings.CoreRestorePackageForTheCurrentUserProvisioning)),
            ("Restore backed-up registry value or startup folder shortcut.", nameof(Strings.CoreRestoreBackedUpRegistryValueOrStartup)),
            ("Re-enable the scheduled task.", nameof(Strings.CoreReEnableTheScheduledTask)),
            ("Reinstall from original vendor installer or Windows restore point.", nameof(Strings.CoreReinstallFromOriginalVendorInstallerOrWindows)),
            ("Manual restore may be required.", nameof(Strings.CoreManualRestoreMayBeRequired)),
            ("needed for OMEN key", nameof(Strings.CoreNeededForOMENKey)),
            ("may affect lighting handoff", nameof(Strings.CoreMayAffectLightingHandoff)),
            ("may affect fan/performance ownership", nameof(Strings.CoreMayAffectFanPerformanceOwnership)),
            ("safe only after OmenCore hardware validation", nameof(Strings.CoreSafeOnlyAfterOmenCoreHardwareValidation)),
            ("Verify OmenCore fan control, keyboard lighting, and OMEN-key behavior before removal.", nameof(Strings.CoreVerifyOmenCoreFanControlKeyboardLightingAnd)),
            ("may affect HP hotkeys", nameof(Strings.CoreMayAffectHPHotkeys)),
            ("May affect HP Fn/media/hotkey behavior.", nameof(Strings.CoreMayAffectHPFnMediaHotkeyBehavior)),
            ("Game Pass/Xbox dependency", nameof(Strings.CoreGamePassXboxDependency)),
            ("May affect Xbox app, Game Pass, or Gaming Services flows.", nameof(Strings.CoreMayAffectXboxAppGamePassOr)),
            ("security component", nameof(Strings.CoreSecurityComponent)),
            ("Security-related component; verify replacement coverage before removal.", nameof(Strings.CoreSecurityRelatedComponentVerifyReplacementCoverageBefore)),
            ("Restore point created successfully.", nameof(Strings.CoreRestorePointCreatedSuccessfully)),
            ("Reusing a recent pre-removal restore point from this session.", nameof(Strings.CoreReusingARecentPreRemovalRestorePoint)),
            ("Restore point creation failed.", nameof(Strings.CoreRestorePointCreationFailed)),
            ("Found {0} bloatware items", nameof(Strings.CoreFoundBloatwareItems)),
            ("Skipped {0}: {1}", nameof(Strings.CoreSkipped)),
            ("Removing {0}...", nameof(Strings.CoreRemoving)),
            ("Removal started for {0}.", nameof(Strings.CoreRemovalStartedFor)),
            ("Removed {0}: {1}", nameof(Strings.CoreRemoved)),
            ("Error removing {0}: {1}", nameof(Strings.CoreErrorRemoving)),
            ("Failed to remove {0}: {1}", nameof(Strings.CoreFailedToRemove)),
            ("Bulk removal canceled. {0} completed, {1} skipped.", nameof(Strings.CoreBulkRemovalCanceledCompletedSkipped)),
            ("Bulk removal canceled at {0}. {1} completed, {2} skipped.", nameof(Strings.CoreBulkRemovalCanceledAtCompletedSkipped)),
            ("Bulk removal failed at {0}. Rolling back previous removals...", nameof(Strings.CoreBulkRemovalFailedAtRollingBackPrevious)),
            ("Rollback complete ({0} restored, {1} skipped).", nameof(Strings.CoreRollbackCompleteRestoredSkipped)),
            ("Rollback partial ({0} restored, {1} failed, {2} skipped).", nameof(Strings.CoreRollbackPartialRestoredFailedSkipped)),
            ("Bulk remove stopped at {0}. {1}", nameof(Strings.CoreBulkRemoveStoppedAt)),
            ("Bulk restore canceled. {0} completed, {1} skipped.", nameof(Strings.CoreBulkRestoreCanceledCompletedSkipped)),
            ("Bulk restore canceled at {0}. {1} completed, {2} skipped.", nameof(Strings.CoreBulkRestoreCanceledAtCompletedSkipped)),
            ("Package is still present after removal attempt ({0}).", nameof(Strings.CorePackageIsStillPresentAfterRemovalAttempt)),
            ("Removal resulted in no state change; package is still present ({0}).", nameof(Strings.CoreRemovalResultedInNoStateChangePackage)),
            ("{0} Command error: {1}", nameof(Strings.CoreCommandError)),
            ("Uninstaller exited with code {0} and app is still present.", nameof(Strings.CoreUninstallerExitedWithCodeAndAppIs)),
            ("Disabling scheduled task for {0}", nameof(Strings.CoreDisablingScheduledTaskFor)),
            ("Restoring {0}...", nameof(Strings.CoreRestoring)),
            ("Successfully restored {0}", nameof(Strings.CoreSuccessfullyRestored)),
            ("Canceled restoring {0}", nameof(Strings.CoreCanceledRestoring)),
            ("Re-enabling scheduled task for {0}", nameof(Strings.CoreReEnablingScheduledTaskFor)),
            ("{0} timed out after {1} seconds and was terminated.", nameof(Strings.CoreTimedOutAfterSecondsAndWasTerminated)),
            ("Checking scheduled task {0}", nameof(Strings.CoreCheckingScheduledTask)),
            ("AppX package: {0}", nameof(Strings.CoreAppXPackage)),
            ("Win32 uninstall registry key: {0}", nameof(Strings.CoreWin32UninstallRegistryKey)),
            ("Win32 uninstall command: {0}", nameof(Strings.CoreWin32UninstallCommand)),
            ("Startup folder item: {0}", nameof(Strings.CoreStartupFolderItem)),
            ("Scheduled task: {0}", nameof(Strings.CoreScheduledTask_32d4b)),
        };

        private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
        private static readonly List<(Regex Pattern, string Key)> Patterns = new();

        static CoreText()
        {
            foreach (var (template, key) in Templates)
            {
                if (!template.Contains('{'))
                {
                    Exact[template] = key;
                    continue;
                }

                var pattern = "^" + Regex.Replace(Regex.Escape(template), @"\\\{\d+}", "(.+?)") + "$";
                Patterns.Add((new Regex(pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant), key));
            }
        }

        public static string Translate(string? text) => Translate(text, 0);

        private static string Translate(string? text, int depth)
        {
            if (string.IsNullOrEmpty(text) || depth > 4)
            {
                return text ?? string.Empty;
            }

            if (Exact.TryGetValue(text, out var exactKey))
            {
                return Strings.Lookup(exactKey) ?? text;
            }

            foreach (var (pattern, key) in Patterns)
            {
                var match = pattern.Match(text);
                if (!match.Success)
                {
                    continue;
                }

                var format = Strings.Lookup(key);
                if (format == null)
                {
                    return text;
                }

                var args = match.Groups.Cast<Group>().Skip(1)
                    .Select(g => (object)Translate(g.Value, depth + 1))
                    .ToArray();
                return string.Format(CultureInfo.CurrentCulture, format, args);
            }

            // Comma-separated lists of known fragments ("all users, provisioned image").
            if (text.Contains(", ", StringComparison.Ordinal))
            {
                var parts = text.Split(", ");
                if (parts.All(p => Exact.ContainsKey(p)))
                {
                    return string.Join(", ", parts.Select(p => Translate(p, depth + 1)));
                }
            }

            return text;
        }

        /// <summary>Display name of an enum value from Core: key Enum{Type}{Value}, else the raw name.</summary>
        public static string EnumName(object? value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            var type = value.GetType();
            if (!type.IsEnum)
            {
                return value.ToString() ?? string.Empty;
            }

            return Strings.Lookup("Enum" + type.Name + value) ?? value.ToString() ?? string.Empty;
        }
    }

    /// <summary>XAML: {Binding Description, Converter={StaticResource CoreTextConverter}}.</summary>
    public sealed class CoreTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string s ? CoreText.Translate(s) : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    /// <summary>
    /// XAML: {Binding RemovalRisk, Converter={StaticResource EnumTextConverter}}. With
    /// ConverterParameter=AllWhenUnknown, an Unknown value reads "All categories" (filter lists).
    /// </summary>
    public sealed class EnumTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Enum e && Equals(parameter, "AllWhenUnknown") && e.ToString() == "Unknown")
            {
                return Strings.CsAllCategories;
            }

            return CoreText.EnumName(value);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
