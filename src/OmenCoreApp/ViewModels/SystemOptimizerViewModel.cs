using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OmenCore.Services;
using OmenCore.Services.SystemOptimizer;
using OmenCore.Services.SystemOptimizer.Optimizations;
using OmenCore.Utils;

namespace OmenCore.ViewModels
{
    /// <summary>
    /// ViewModel for the System Optimizer view.
    /// </summary>
    public class SystemOptimizerViewModel : INotifyPropertyChanged
    {
        private readonly LoggingService _logger;
        private readonly SystemOptimizerService _optimizerService;
        
        private bool _isLoading;
        private string _statusMessage = "Done";
        private OptimizationState? _currentState;
        private bool _hasUnsavedChanges;
        private int _operationCurrentStep;
        private int _operationTotalSteps;
        private string _operationProgressLabel = string.Empty;
        private readonly DispatcherTimer _verificationTimer;
        private DateTime? _lastVerifiedAt;
        private string _verificationSummary = global::OmenCore.Localization.Strings.CsVerificationPending;
        private bool _hasStateDrift;
        private OptimizationDriftSummary _lastDriftSummary = new();
        private string _driftWarningRollup = global::OmenCore.Localization.Strings.CsNoDriftExplanationsYet;
        private string _lastExportedReportPath = string.Empty;
        private OptimizationState? _expectedStateForDrift;
        private string _preflightSummary = global::OmenCore.Localization.Strings.CsPreflightNotLoaded;
        private string _preflightWarningRollup = global::OmenCore.Localization.Strings.CsNoAdditionalPreflightWarnings;
        private bool _preflightHasHighRisk;
        private int _preflightHighRiskCount;
        
        public event PropertyChangedEventHandler? PropertyChanged;

        public SystemOptimizerViewModel(LoggingService logger)
        {
            _logger = logger;
            
            // Initialize optimizer (creates its own backup service internally)
            _optimizerService = new SystemOptimizerService(logger);
            
            // Subscribe to status updates — marshal to UI thread so StatusMessage
            // can be safely set from background threads (avoids cross-thread binding errors).
            _optimizerService.StatusChanged += status => 
            {
                DispatcherHelper.RunOnUiThread(() => HandleServiceStatus(status));
            };
            
            // Initialize collections
            PowerOptimizations = new ObservableCollection<OptimizationItem>();
            ServiceOptimizations = new ObservableCollection<OptimizationItem>();
            NetworkOptimizations = new ObservableCollection<OptimizationItem>();
            InputOptimizations = new ObservableCollection<OptimizationItem>();
            VisualOptimizations = new ObservableCollection<OptimizationItem>();
            StorageOptimizations = new ObservableCollection<OptimizationItem>();
            
            // Initialize commands
            RefreshCommand = new RelayCommand(_ => _ = RefreshStateAsync());
            ApplyGamingMaxCommand = new RelayCommand(_ => _ = ApplyGamingMaxAsync());
            ApplyBalancedCommand = new RelayCommand(_ => _ = ApplyBalancedAsync());
            RevertAllCommand = new RelayCommand(_ => _ = RevertAllAsync());
            ApplyPowerCommand = new RelayCommand(_ => _ = ApplyCategoryAsync("Power & Performance", PowerOptimizations), _ => CanApplyCategory(PowerOptimizations));
            ApplyServicesCommand = new RelayCommand(_ => _ = ApplyCategoryAsync("Windows Services", ServiceOptimizations), _ => CanApplyCategory(ServiceOptimizations));
            ApplyNetworkCommand = new RelayCommand(_ => _ = ApplyCategoryAsync("Network & Latency", NetworkOptimizations), _ => CanApplyCategory(NetworkOptimizations));
            ApplyInputCommand = new RelayCommand(_ => _ = ApplyCategoryAsync("Input & Gaming", InputOptimizations), _ => CanApplyCategory(InputOptimizations));
            ApplyVisualCommand = new RelayCommand(_ => _ = ApplyCategoryAsync("Visual Effects", VisualOptimizations), _ => CanApplyCategory(VisualOptimizations));
            ApplyStorageCommand = new RelayCommand(_ => _ = ApplyCategoryAsync("Storage & Disk", StorageOptimizations), _ => CanApplyCategory(StorageOptimizations));
            RefreshPreflightCommand = new RelayCommand(_ => _ = RefreshPreflightAsync());
            ExportOptimizationReportCommand = new RelayCommand(_ => _ = ExportOptimizationReportAsync(), _ => !IsLoading);
            OpenExportedReportCommand = new RelayCommand(_ => OpenExportedReport(), _ => CanOpenExportedReport);
            CopyExportedReportPathCommand = new RelayCommand(_ => CopyExportedReportPath(), _ => CanCopyExportedReportPath);

            _verificationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromHours(1)
            };
            _verificationTimer.Tick += async (_, _) => await RunBackgroundVerificationAsync();
            _verificationTimer.Start();
            
            // Load initial state
            _ = RefreshStateAsync();
        }

        // ========== PROPERTIES ==========

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotLoading)); RaiseCategoryCommandStates(); }
        }

        public bool IsNotLoading => !_isLoading;

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            set { _hasUnsavedChanges = value; OnPropertyChanged(); }
        }

        public int OperationCurrentStep
        {
            get => _operationCurrentStep;
            private set { _operationCurrentStep = value; OnPropertyChanged(); OnPropertyChanged(nameof(OperationProgressValue)); OnPropertyChanged(nameof(OperationProgressText)); OnPropertyChanged(nameof(HasOperationProgress)); }
        }

        public int OperationTotalSteps
        {
            get => _operationTotalSteps;
            private set { _operationTotalSteps = value; OnPropertyChanged(); OnPropertyChanged(nameof(OperationProgressMaximum)); OnPropertyChanged(nameof(OperationProgressText)); OnPropertyChanged(nameof(HasOperationProgress)); }
        }

        public string OperationProgressLabel
        {
            get => _operationProgressLabel;
            private set { _operationProgressLabel = value; OnPropertyChanged(); }
        }

        public bool HasOperationProgress => OperationTotalSteps > 0;
        public double OperationProgressValue => OperationCurrentStep;
        public double OperationProgressMaximum => OperationTotalSteps > 0 ? OperationTotalSteps : 1;
        public string OperationProgressText => HasOperationProgress ? string.Format(global::OmenCore.Localization.Strings.CsStep, OperationCurrentStep, OperationTotalSteps) : string.Empty;

        public int ActiveOptimizationCount => _currentState?.ActiveCount ?? 0;
        public int TotalOptimizationCount => _currentState?.TotalCount ?? 0;
        public DateTime? LastVerifiedAt
        {
            get => _lastVerifiedAt;
            private set
            {
                _lastVerifiedAt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastVerifiedText));
            }
        }

        public string LastVerifiedText => LastVerifiedAt.HasValue
            ? string.Format(global::OmenCore.Localization.Strings.CsLastVerified, LastVerifiedAt.Value)
            : global::OmenCore.Localization.Strings.CsVerificationPending;

        public string VerificationSummary
        {
            get => _verificationSummary;
            private set { _verificationSummary = value; OnPropertyChanged(); }
        }

        public bool HasStateDrift
        {
            get => _hasStateDrift;
            private set { _hasStateDrift = value; OnPropertyChanged(); }
        }

        public string DriftSummaryText => _lastDriftSummary.HasDrift
            ? _lastDriftSummary.OneLinerSummary
            : global::OmenCore.Localization.Strings.CsNoDriftDetectedSinceLastVerification;

        public string DriftWarningRollup
        {
            get => _driftWarningRollup;
            private set { _driftWarningRollup = value; OnPropertyChanged(); }
        }

        public string LastExportedReportPath
        {
            get => _lastExportedReportPath;
            private set
            {
                _lastExportedReportPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasExportedReport));
                OnPropertyChanged(nameof(CanOpenExportedReport));
                OnPropertyChanged(nameof(CanCopyExportedReportPath));
            }
        }

        public bool HasExportedReport => !string.IsNullOrWhiteSpace(LastExportedReportPath);
        public bool CanOpenExportedReport => SystemOptimizerReportActionGuard.CanOpenReport(LastExportedReportPath);
        public bool CanCopyExportedReportPath => SystemOptimizerReportActionGuard.CanCopyPath(LastExportedReportPath);

        public string OptimizationSummary => 
            string.Format(global::OmenCore.Localization.Strings.CsOfOptimizationsActive, ActiveOptimizationCount, TotalOptimizationCount);

        public string PreflightSummary
        {
            get => _preflightSummary;
            private set { _preflightSummary = value; OnPropertyChanged(); }
        }

        public string PreflightWarningRollup
        {
            get => _preflightWarningRollup;
            private set { _preflightWarningRollup = value; OnPropertyChanged(); }
        }

        public bool PreflightHasHighRisk
        {
            get => _preflightHasHighRisk;
            private set { _preflightHasHighRisk = value; OnPropertyChanged(); OnPropertyChanged(nameof(PreflightRiskBannerText)); }
        }

        public int PreflightHighRiskCount
        {
            get => _preflightHighRiskCount;
            private set { _preflightHighRiskCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PreflightRiskBannerText)); }
        }

        public string PreflightRiskBannerText => PreflightHasHighRisk
            ? string.Format(global::OmenCore.Localization.Strings.CsPreflightFoundHighRiskOptimizationSReview, PreflightHighRiskCount)
            : global::OmenCore.Localization.Strings.CsPreflightContainsNoHighRiskOptimizations;

        // Optimization Collections
        public ObservableCollection<OptimizationItem> PowerOptimizations { get; }
        public ObservableCollection<OptimizationItem> ServiceOptimizations { get; }
        public ObservableCollection<OptimizationItem> NetworkOptimizations { get; }
        public ObservableCollection<OptimizationItem> InputOptimizations { get; }
        public ObservableCollection<OptimizationItem> VisualOptimizations { get; }
        public ObservableCollection<OptimizationItem> StorageOptimizations { get; }

        // ========== COMMANDS ==========

        public ICommand RefreshCommand { get; }
        public ICommand ApplyGamingMaxCommand { get; }
        public ICommand ApplyBalancedCommand { get; }
        public ICommand RevertAllCommand { get; }
        public ICommand ApplyPowerCommand { get; }
        public ICommand ApplyServicesCommand { get; }
        public ICommand ApplyNetworkCommand { get; }
        public ICommand ApplyInputCommand { get; }
        public ICommand ApplyVisualCommand { get; }
        public ICommand ApplyStorageCommand { get; }
        public ICommand RefreshPreflightCommand { get; }
        public ICommand ExportOptimizationReportCommand { get; }
        public ICommand OpenExportedReportCommand { get; }
        public ICommand CopyExportedReportPathCommand { get; }

        // ========== METHODS ==========

        public async Task RefreshStateAsync(bool showOverlay = true)
        {
            try
            {
                if (showOverlay)
                {
                    IsLoading = true;
                }

                SetStatusAction(showOverlay ? global::OmenCore.Localization.Strings.CsScanningSystemState : global::OmenCore.Localization.Strings.CsRefreshingOptimizerState);
                
                _currentState = await _optimizerService.VerifyStateAsync();
                _expectedStateForDrift = _currentState;
                LastVerifiedAt = _currentState.LastChecked;
                VerificationSummary = global::OmenCore.Localization.Strings.CsVerifiedAgainstCurrentSystemState;
                HasStateDrift = false;
                _lastDriftSummary = new OptimizationDriftSummary();
                DriftWarningRollup = global::OmenCore.Localization.Strings.CsNoDriftExplanationsYet;
                OnPropertyChanged(nameof(DriftSummaryText));

                await RefreshPreflightAsync(showStatus: false);
                
                UpdateOptimizationCollections();
                
                OnPropertyChanged(nameof(ActiveOptimizationCount));
                OnPropertyChanged(nameof(TotalOptimizationCount));
                OnPropertyChanged(nameof(OptimizationSummary));
                
                SetStatusDone();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to refresh optimization state: {ex.Message}");
                SetStatusFailed(ex.Message);
            }
            finally
            {
                if (showOverlay)
                {
                    IsLoading = false;
                }
            }
        }

        private void UpdateOptimizationCollections()
        {
            if (_currentState == null) return;
            
            DispatcherHelper.RunOnUiThread(() =>
            {
                // Power
                PowerOptimizations.Clear();
                PowerOptimizations.Add(new OptimizationItem
                {
                    Id = "power_ultimate_perf",
                    Name = "Ultimate Performance Plan",
                    Description = global::OmenCore.Localization.Strings.CsSetsWindowsPowerPlanToUltimatePerformance,
                    IsEnabled = _currentState.Power.UltimatePerformancePlan,
                    Risk = OptimizationRisk.Low,
                    Category = "Power",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                PowerOptimizations.Add(new OptimizationItem
                {
                    Id = "power_gpu_scheduling",
                    Name = "Hardware GPU Scheduling",
                    Description = global::OmenCore.Localization.Strings.CsEnablesHardwareAcceleratedGPUSchedulingWindows10,
                    IsEnabled = _currentState.Power.HardwareGpuScheduling,
                    Risk = OptimizationRisk.Low,
                    Category = "Power",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                PowerOptimizations.Add(new OptimizationItem
                {
                    Id = "power_game_mode",
                    Name = "Game Mode",
                    Description = global::OmenCore.Localization.Strings.CsEnablesWindowsGameModeForBetterGaming,
                    IsEnabled = _currentState.Power.GameModeEnabled,
                    Risk = OptimizationRisk.Low,
                    Category = "Power",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                PowerOptimizations.Add(new OptimizationItem
                {
                    Id = "power_foreground_priority",
                    Name = "Foreground Priority Boost",
                    Description = global::OmenCore.Localization.Strings.CsIncreasesCPUPriorityForForegroundApplications,
                    IsEnabled = _currentState.Power.ForegroundPriority,
                    Risk = OptimizationRisk.Low,
                    Category = "Power",
                    OnToggleAsync = ToggleOptimizationAsync
                });

                // Services
                ServiceOptimizations.Clear();
                ServiceOptimizations.Add(new OptimizationItem
                {
                    Id = "service_telemetry",
                    Name = "Disable Telemetry",
                    Description = global::OmenCore.Localization.Strings.CsDisablesWindowsTelemetryDataCollectionDiagTrackService,
                    IsEnabled = _currentState.Services.TelemetryDisabled,
                    Risk = OptimizationRisk.Medium,
                    Category = "Services",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                ServiceOptimizations.Add(new OptimizationItem
                {
                    Id = "service_sysmain",
                    Name = "Disable SysMain (Superfetch)",
                    Description = global::OmenCore.Localization.Strings.CsDisablesSuperfetchServiceRecommendedForSSDSystems,
                    IsEnabled = _currentState.Services.SysMainDisabled,
                    Risk = OptimizationRisk.Medium,
                    Category = "Services",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                ServiceOptimizations.Add(new OptimizationItem
                {
                    Id = "service_search",
                    Name = "Disable Search Indexing",
                    Description = global::OmenCore.Localization.Strings.CsDisablesWindowsSearchIndexingForLowerDisk,
                    IsEnabled = _currentState.Services.SearchIndexingDisabled,
                    Risk = OptimizationRisk.Medium,
                    Warning = global::OmenCore.Localization.Strings.CsMaySlowDownFileSearches,
                    Category = "Services",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                ServiceOptimizations.Add(new OptimizationItem
                {
                    Id = "service_diagtrack",
                    Name = "Disable Connected Experiences",
                    Description = global::OmenCore.Localization.Strings.CsDisablesConnectedUserExperiencesAndTelemetryService,
                    IsEnabled = _currentState.Services.DiagTrackDisabled,
                    Risk = OptimizationRisk.Medium,
                    Category = "Services",
                    OnToggleAsync = ToggleOptimizationAsync
                });

                // Network
                NetworkOptimizations.Clear();
                NetworkOptimizations.Add(new OptimizationItem
                {
                    Id = "network_tcp_nodelay",
                    Name = "TCP No Delay",
                    Description = global::OmenCore.Localization.Strings.CsDisablesTCPDelayedAcknowledgmentsForLowerLatency,
                    IsEnabled = _currentState.Network.TcpNoDelay,
                    Risk = OptimizationRisk.Low,
                    Category = "Network",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                NetworkOptimizations.Add(new OptimizationItem
                {
                    Id = "network_tcp_ack",
                    Name = "TCP ACK Optimization",
                    Description = global::OmenCore.Localization.Strings.CsOptimizesTCPACKFrequencyForFasterResponses,
                    IsEnabled = _currentState.Network.TcpAckFrequency,
                    Risk = OptimizationRisk.Low,
                    Category = "Network",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                NetworkOptimizations.Add(new OptimizationItem
                {
                    Id = "network_delivery_opt",
                    Name = "Disable P2P Updates",
                    Description = global::OmenCore.Localization.Strings.CsDisablesDeliveryOptimizationP2PSharing,
                    IsEnabled = _currentState.Network.DeliveryOptimizationDisabled,
                    Risk = OptimizationRisk.Low,
                    Category = "Network",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                NetworkOptimizations.Add(new OptimizationItem
                {
                    Id = "network_nagle",
                    Name = "Disable Nagle Algorithm",
                    Description = global::OmenCore.Localization.Strings.CsReducesTCPBufferingDelayHelpfulForOnline,
                    IsEnabled = _currentState.Network.NagleDisabled,
                    Risk = OptimizationRisk.Low,
                    Category = "Network",
                    OnToggleAsync = ToggleOptimizationAsync
                });

                // Input
                InputOptimizations.Clear();
                InputOptimizations.Add(new OptimizationItem
                {
                    Id = "input_mouse_accel",
                    Name = "Disable Mouse Acceleration",
                    Description = global::OmenCore.Localization.Strings.CsDisablesEnhancePointerPrecisionFor11,
                    IsEnabled = _currentState.Input.MouseAccelerationDisabled,
                    Risk = OptimizationRisk.Low,
                    Category = "Input",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                InputOptimizations.Add(new OptimizationItem
                {
                    Id = "input_game_dvr",
                    Name = "Disable Game DVR",
                    Description = global::OmenCore.Localization.Strings.CsDisablesBackgroundGameRecordingForBetterPerformance,
                    IsEnabled = _currentState.Input.GameDvrDisabled,
                    Risk = OptimizationRisk.Low,
                    Category = "Input",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                InputOptimizations.Add(new OptimizationItem
                {
                    Id = "input_game_bar",
                    Name = "Disable Xbox Game Bar",
                    Description = global::OmenCore.Localization.Strings.CsDisablesTheXboxGameBarOverlay,
                    IsEnabled = _currentState.Input.GameBarDisabled,
                    Risk = OptimizationRisk.Low,
                    Category = "Input",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                InputOptimizations.Add(new OptimizationItem
                {
                    Id = "input_fullscreen_opt",
                    Name = "Fullscreen Optimizations",
                    Description = global::OmenCore.Localization.Strings.CsConfiguresFullscreenOptimizationsForBetterCompatibility,
                    IsEnabled = _currentState.Input.FullscreenOptimizationsDisabled,
                    Risk = OptimizationRisk.Low,
                    Category = "Input",
                    OnToggleAsync = ToggleOptimizationAsync
                });

                // Visual
                VisualOptimizations.Clear();
                VisualOptimizations.Add(new OptimizationItem
                {
                    Id = "visual_transparency",
                    Name = "Disable Transparency",
                    Description = global::OmenCore.Localization.Strings.CsDisablesWindowsTransparencyEffects,
                    IsEnabled = _currentState.Visual.TransparencyDisabled,
                    Risk = OptimizationRisk.Low,
                    Category = "Visual",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                VisualOptimizations.Add(new OptimizationItem
                {
                    Id = "visual_animations",
                    Name = "Disable Animations",
                    Description = global::OmenCore.Localization.Strings.CsDisablesWindowAnimationsForSnappierUI,
                    IsEnabled = _currentState.Visual.AnimationsDisabled,
                    Risk = OptimizationRisk.Low,
                    Category = "Visual",
                    OnToggleAsync = ToggleOptimizationAsync
                });

                // Storage
                StorageOptimizations.Clear();
                if (_currentState.Storage.IsSsd)
                {
                    StorageOptimizations.Add(new OptimizationItem
                    {
                        Id = "storage_trim",
                        Name = "TRIM for SSD",
                        Description = global::OmenCore.Localization.Strings.CsEnablesTRIMCommandForSSDHealthAnd,
                        IsEnabled = _currentState.Storage.TrimEnabled,
                        Risk = OptimizationRisk.Low,
                        Category = "Storage",
                        OnToggleAsync = ToggleOptimizationAsync
                    });
                }
                StorageOptimizations.Add(new OptimizationItem
                {
                    Id = "storage_last_access",
                    Name = "Disable Last Access Timestamps",
                    Description = global::OmenCore.Localization.Strings.CsDisablesNTFSLastAccessTimeUpdatesFor,
                    IsEnabled = _currentState.Storage.LastAccessDisabled,
                    Risk = OptimizationRisk.Low,
                    Category = "Storage",
                    OnToggleAsync = ToggleOptimizationAsync
                });
                StorageOptimizations.Add(new OptimizationItem
                {
                    Id = "storage_8dot3",
                    Name = "Disable 8.3 Names",
                    Description = global::OmenCore.Localization.Strings.CsDisablesLegacyShortFilenameCreation,
                    IsEnabled = _currentState.Storage.ShortNamesDisabled,
                    Risk = OptimizationRisk.Low,
                    Warning = global::OmenCore.Localization.Strings.CsMayBreakVeryOldSoftware,
                    Category = "Storage",
                    OnToggleAsync = ToggleOptimizationAsync
                });

                ApplyRiskAssessmentMetadata(PowerOptimizations);
                ApplyRiskAssessmentMetadata(ServiceOptimizations);
                ApplyRiskAssessmentMetadata(NetworkOptimizations);
                ApplyRiskAssessmentMetadata(InputOptimizations);
                ApplyRiskAssessmentMetadata(VisualOptimizations);
                ApplyRiskAssessmentMetadata(StorageOptimizations);
                RaiseCategoryCommandStates();
            });
        }

        private async Task ToggleOptimizationAsync(OptimizationItem item, bool desiredState)
        {
            try
            {
                if (item.IsApplying)
                {
                    return;
                }

                item.IsApplying = true;
                SetStatusAction(desiredState ? $"Applying {item.Name}" : $"Reverting {item.Name}");
                
                OptimizationResult result;
                
                if (desiredState)
                {
                    result = await _optimizerService.ApplyOptimizationAsync(item.Id);
                }
                else
                {
                    result = await _optimizerService.RevertOptimizationAsync(item.Id);
                }
                
                if (result.Success)
                {
                    item.IsEnabled = desiredState;
                    SetStatusDone($"{item.Name} {(item.IsEnabled ? "enabled" : "disabled")}");
                    
                    if (result.RequiresReboot)
                    {
                        SetStatusDone(string.Format(global::OmenCore.Localization.Strings.CsRebootRequired_d19bf, item.Name, (item.IsEnabled ? "enabled" : "disabled")));
                    }
                }
                else
                {
                    SetStatusFailed(result.ErrorMessage);
                }
                
                // Refresh authoritative state without blocking the whole page for a single-item toggle.
                await RefreshStateAsync(showOverlay: false);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to toggle optimization: {ex.Message}");
                SetStatusFailed(ex.Message);
            }
            finally
            {
                item.IsApplying = false;
            }
        }

        private async Task ApplyGamingMaxAsync()
        {
            var preflight = await _optimizerService.GeneratePreflightReportAsync(includeHighRisk: true);

            var msgResult = MessageBox.Show(
                global::OmenCore.Localization.Strings.CsThisWillApplyALLGamingOptimizationsFor +
                $"{SystemOptimizerPreflightFormatter.BuildSummary(preflight)}\n" +
                $"{SystemOptimizerPreflightFormatter.BuildWarningRollup(preflight)}\n\n" +
                global::OmenCore.Localization.Strings.CsASystemRestorePointWillBeCreated +
                global::OmenCore.Localization.Strings.CsContinue,
                global::OmenCore.Localization.Strings.CsApplyGamingMaximum,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            
            if (msgResult != MessageBoxResult.Yes) return;
            
            try
            {
                IsLoading = true;
                var results = await _optimizerService.ApplyGamingMaximumAsync();
                
                var successCount = results.Count(r => r.Success);
                var failCount = results.Count - successCount;
                var rebootNeeded = results.Any(r => r.RequiresReboot && r.Success);
                
                SetStatusDone(string.Format(global::OmenCore.Localization.Strings.CsAppliedOptimizations_2, successCount, results.Count));
                
                if (failCount > 0)
                {
                    SetStatusDone(string.Format(global::OmenCore.Localization.Strings.CsAppliedOptimizationsFailed, successCount, results.Count, failCount));
                }
                
                if (rebootNeeded)
                {
                    var reboot = MessageBox.Show(
                        global::OmenCore.Localization.Strings.CsSomeChangesRequireARebootToTake,
                        global::OmenCore.Localization.Strings.CsRebootRequired,
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (reboot == MessageBoxResult.Yes)
                    {
                        System.Diagnostics.Process.Start("shutdown", "/r /t 0");
                    }
                }
                
                await RefreshStateAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to apply gaming max: {ex.Message}");
                SetStatusFailed(ex.Message);
                MessageBox.Show($"Failed to apply optimizations:\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ApplyCategoryAsync(string categoryName, ObservableCollection<OptimizationItem> optimizations)
        {
            var pendingItems = optimizations.Where(item => !item.IsEnabled).ToList();
            if (!pendingItems.Any())
            {
                SetStatusDone(string.Format(global::OmenCore.Localization.Strings.CsIsAlreadyFullyApplied, categoryName));
                return;
            }

            var confirmation = MessageBox.Show(
                string.Format(global::OmenCore.Localization.Strings.CsThisWillApplyOptimizationSInContinue, pendingItems.Count, categoryName),
                $"Apply {categoryName}",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                IsLoading = true;
                OperationTotalSteps = pendingItems.Count;

                var successCount = 0;
                var failCount = 0;

                for (var index = 0; index < pendingItems.Count; index++)
                {
                    var item = pendingItems[index];
                    OperationCurrentStep = index + 1;
                    OperationProgressLabel = $"Applying {item.Name}";
                    SetStatusAction($"Applying {item.Name}");

                    var result = await _optimizerService.ApplyOptimizationAsync(item.Id);
                    if (result.Success)
                    {
                        successCount++;
                    }
                    else
                    {
                        failCount++;
                    }
                }

                if (failCount > 0)
                {
                    SetStatusDone(string.Format(global::OmenCore.Localization.Strings.CsAppliedItemsInFailed, successCount, pendingItems.Count, categoryName, failCount));
                }
                else
                {
                    SetStatusDone(string.Format(global::OmenCore.Localization.Strings.CsAppliedOptimizationS, successCount, categoryName));
                }

                await RefreshStateAsync(showOverlay: false);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to apply {categoryName}: {ex.Message}");
                SetStatusFailed(ex.Message);
            }
            finally
            {
                ClearOperationProgress();
                IsLoading = false;
            }
        }

        private async Task ApplyBalancedAsync()
        {
            var preflight = await _optimizerService.GeneratePreflightReportAsync(includeHighRisk: false);

            var msgResult = MessageBox.Show(
                global::OmenCore.Localization.Strings.CsThisWillApplyRecommendedOptimizationsWithMinimal +
                $"{SystemOptimizerPreflightFormatter.BuildSummary(preflight)}\n" +
                $"{SystemOptimizerPreflightFormatter.BuildWarningRollup(preflight)}\n\n" +
                global::OmenCore.Localization.Strings.CsASystemRestorePointWillBeCreated +
                global::OmenCore.Localization.Strings.CsContinue,
                global::OmenCore.Localization.Strings.CsApplyBalancedOptimizations,
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            
            if (msgResult != MessageBoxResult.Yes) return;
            
            try
            {
                IsLoading = true;
                var results = await _optimizerService.ApplyBalancedAsync();
                
                var successCount = results.Count(r => r.Success);
                SetStatusDone(string.Format(global::OmenCore.Localization.Strings.CsAppliedOptimizations_2, successCount, results.Count));
                
                await RefreshStateAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to apply balanced: {ex.Message}");
                SetStatusFailed(ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task RevertAllAsync()
        {
            var msgResult = MessageBox.Show(
                global::OmenCore.Localization.Strings.CsThisWillRevertALLOptimizationsToWindows

,
                global::OmenCore.Localization.Strings.CsRevertAllOptimizations,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (msgResult != MessageBoxResult.Yes) return;
            
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                IsLoading = true;
                var results = await _optimizerService.RevertAllAsync(cts.Token);
                
                var successCount = results.Count(r => r.Success);
                SetStatusDone(string.Format(global::OmenCore.Localization.Strings.CsRevertedOptimizations, successCount, results.Count));
                
                await RefreshStateAsync();
            }
            catch (OperationCanceledException)
            {
                _logger.Error("Revert operation timed out after 60 seconds");
                SetStatusFailed(global::OmenCore.Localization.Strings.CsRevertTimedOutCheckSystemStateManually);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to revert: {ex.Message}");
                SetStatusFailed(ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void SetStatusAction(string action)
        {
            StatusMessage = $"{action.Trim().TrimEnd('.')}...";
        }

        private void SetStatusDone(string? details = null)
        {
            StatusMessage = string.IsNullOrWhiteSpace(details) ? "Done" : $"Done: {details}";
        }

        private void SetStatusFailed(string? reason)
        {
            StatusMessage = $"Failed: {reason}";
        }

        private void HandleServiceStatus(string status)
        {
            StatusMessage = status;
            UpdateOperationProgressFromStatus(status);
        }

        private void UpdateOperationProgressFromStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            var match = Regex.Match(status, @"^\[(\d+)(?:-\d+)?\/(\d+)\]\s*(.+)$");
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out var currentStep) &&
                int.TryParse(match.Groups[2].Value, out var totalSteps))
            {
                OperationCurrentStep = currentStep;
                OperationTotalSteps = totalSteps;
                OperationProgressLabel = match.Groups[3].Value.Trim();
                return;
            }

            if (status.StartsWith("Revert complete", StringComparison.OrdinalIgnoreCase) ||
                status.StartsWith("Failed:", StringComparison.OrdinalIgnoreCase) ||
                status.StartsWith("Done", StringComparison.OrdinalIgnoreCase) ||
                status.StartsWith("Complete:", StringComparison.OrdinalIgnoreCase))
            {
                ClearOperationProgress();
            }
        }

        private void ClearOperationProgress()
        {
            OperationCurrentStep = 0;
            OperationTotalSteps = 0;
            OperationProgressLabel = string.Empty;
            RaiseCategoryCommandStates();
        }

        private async Task RunBackgroundVerificationAsync()
        {
            if (IsLoading || _currentState == null)
            {
                return;
            }

            try
            {
                var expectedState = _currentState;
                var verifiedState = await _optimizerService.VerifyStateAsync();
                LastVerifiedAt = verifiedState.LastChecked;

                var driftSummary = SystemOptimizerService.GetDriftExplanations(expectedState, verifiedState);
                if (!driftSummary.HasDrift)
                {
                    VerificationSummary = global::OmenCore.Localization.Strings.CsVerifiedNoDriftDetected;
                    HasStateDrift = false;
                    _lastDriftSummary = driftSummary;
                    DriftWarningRollup = global::OmenCore.Localization.Strings.CsNoDriftExplanationsYet;
                    OnPropertyChanged(nameof(DriftSummaryText));
                    return;
                }

                var autoCorrectionResults = await _optimizerService.CorrectMinorDriftAsync(expectedState, verifiedState);
                if (autoCorrectionResults.Any(result => result.Success))
                {
                    verifiedState = await _optimizerService.VerifyStateAsync();
                    LastVerifiedAt = verifiedState.LastChecked;
                    driftSummary = SystemOptimizerService.GetDriftExplanations(expectedState, verifiedState);
                }

                _currentState = verifiedState;
                _expectedStateForDrift = expectedState;
                UpdateOptimizationCollections();
                OnPropertyChanged(nameof(ActiveOptimizationCount));
                OnPropertyChanged(nameof(TotalOptimizationCount));
                OnPropertyChanged(nameof(OptimizationSummary));

                _lastDriftSummary = driftSummary;
                DriftWarningRollup = driftSummary.HasDrift
                    ? string.Join(" | ", driftSummary.DriftedItems.Take(3).Select(d => d.Explanation))
                    : global::OmenCore.Localization.Strings.CsNoDriftExplanationsYet;
                OnPropertyChanged(nameof(DriftSummaryText));

                HasStateDrift = driftSummary.HasDrift;
                VerificationSummary = HasStateDrift
                    ? string.Format(global::OmenCore.Localization.Strings.CsDriftDetected, string.Join(", ", driftSummary.DriftedItems.Select(d => d.Name).Take(3)))
                    : string.Format(global::OmenCore.Localization.Strings.CsVerifiedAutoCorrectedServiceSettingS, autoCorrectionResults.Count(result => result.Success));

                await RefreshPreflightAsync(showStatus: false);
            }
            catch (Exception ex)
            {
                _logger.Error($"Background optimizer verification failed: {ex.Message}");
                VerificationSummary = global::OmenCore.Localization.Strings.CsVerificationFailed;
            }
        }

        private async Task RefreshPreflightAsync(bool showStatus = true)
        {
            try
            {
                if (showStatus)
                {
                    SetStatusAction(global::OmenCore.Localization.Strings.CsRefreshingOptimizerPreflight);
                }

                var report = await _optimizerService.GeneratePreflightReportAsync(includeHighRisk: true);
                PreflightSummary = SystemOptimizerPreflightFormatter.BuildSummary(report);
                PreflightWarningRollup = SystemOptimizerPreflightFormatter.BuildWarningRollup(report);
                PreflightHasHighRisk = report.HasHighRisk;
                PreflightHighRiskCount = report.HighRiskCount;

                if (showStatus)
                {
                    SetStatusDone(global::OmenCore.Localization.Strings.CsPreflightUpdated);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to refresh preflight: {ex.Message}");
                if (showStatus)
                {
                    SetStatusFailed(string.Format(global::OmenCore.Localization.Strings.CsPreflightRefreshFailed, ex.Message));
                }
            }
        }

        private async Task ExportOptimizationReportAsync()
        {
            try
            {
                SetStatusAction(global::OmenCore.Localization.Strings.CsExportingOptimizationReport);
                var exportPath = await _optimizerService.ExportOptimizationReportAsync(_expectedStateForDrift);
                if (string.IsNullOrWhiteSpace(exportPath))
                {
                    SetStatusFailed(global::OmenCore.Localization.Strings.CsCouldNotExportOptimizationReport);
                    return;
                }

                LastExportedReportPath = exportPath;
                SetStatusDone(string.Format(global::OmenCore.Localization.Strings.CsReportExported, exportPath));
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to export optimization report: {ex.Message}");
                SetStatusFailed(string.Format(global::OmenCore.Localization.Strings.CsReportExportFailed, ex.Message));
            }
        }

        private void OpenExportedReport()
        {
            var path = SystemOptimizerReportActionGuard.NormalizePath(LastExportedReportPath);
            if (!SystemOptimizerReportActionGuard.CanOpenReport(path))
            {
                SetStatusFailed(global::OmenCore.Localization.Strings.CsReportFileNotFound);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });
                SetStatusDone(global::OmenCore.Localization.Strings.CsOpenedExportedReport);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to open optimization report: {ex.Message}");
                SetStatusFailed(string.Format(global::OmenCore.Localization.Strings.CsCouldNotOpenReport, ex.Message));
            }
        }

        private void CopyExportedReportPath()
        {
            var path = SystemOptimizerReportActionGuard.NormalizePath(LastExportedReportPath);
            if (!SystemOptimizerReportActionGuard.CanCopyPath(path))
            {
                SetStatusFailed(global::OmenCore.Localization.Strings.CsNoReportPathToCopy);
                return;
            }

            try
            {
                Clipboard.SetText(path);
                SetStatusDone(global::OmenCore.Localization.Strings.CsCopiedReportPathToClipboard);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to copy report path: {ex.Message}");
                SetStatusFailed(string.Format(global::OmenCore.Localization.Strings.CsCouldNotCopyReportPath, ex.Message));
            }
        }

        private bool CanApplyCategory(ObservableCollection<OptimizationItem> optimizations)
        {
            return !IsLoading && optimizations.Any(item => !item.IsEnabled);
        }

        private void RaiseCategoryCommandStates()
        {
            (ApplyPowerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyServicesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyNetworkCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyInputCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyVisualCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyStorageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportOptimizationReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenExportedReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CopyExportedReportPathCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private static void ApplyRiskAssessmentMetadata(IEnumerable<OptimizationItem> items)
        {
            foreach (var item in items)
            {
                var details = GetRiskAssessment(item.Id);
                item.DetailedDescription = details.DetailedDescription;
                item.AffectedRegistryKeys = details.AffectedRegistryKeys;
                item.ServiceNamesAffected = details.ServiceNamesAffected;
                item.UndoInstructions = details.UndoInstructions;
            }
        }

        private static OptimizationRiskAssessment GetRiskAssessment(string optimizationId)
        {
            return optimizationId switch
            {
                "power_ultimate_perf" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsSwitchesTheActiveWindowsPowerPlanTo,
                    Array.Empty<string>(),
                    new[] { "powercfg /setactive e9a42b02-d5df-448d-aa00-03f14749eb61", global::OmenCore.Localization.Strings.CsFallbackPowercfgSetactive8c5e7fdaE8bf4a969a85 },
                    global::OmenCore.Localization.Strings.CsRestoreTheBalancedPlanWithPowercfgSetactive),
                "power_gpu_scheduling" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsEnablesHardwareAcceleratedGPUSchedulingSoWindows,
                    new[] { @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode" },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsSetHwSchModeBackTo0OrRemove),
                "power_game_mode" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsTurnsOnWindowsGameModeSoThe,
                    new[] { @"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled" },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsSetAutoGameModeEnabledTo0OrRemoveThe),
                "power_foreground_priority" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsRaisesForegroundSchedulingPrioritySoTheActive,
                    new[] { @"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl\Win32PrioritySeparation" },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsRestoreTheDefaultWin32PrioritySeparationValueOrRemove),
                "service_telemetry" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsDisablesWindowsTelemetryPolicyValuesThatControl,
                    new[]
                    {
                        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry",
                        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection\AllowTelemetry"
                    },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsRemoveTheAllowTelemetryOverridesToReturnTo),
                "service_sysmain" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsStopsAndDisablesSysMainSoWindowsStops,
                    Array.Empty<string>(),
                    new[] { global::OmenCore.Localization.Strings.CsSysMainService },
                    global::OmenCore.Localization.Strings.CsSetTheSysMainServiceBackToAutomatic),
                "service_search" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsStopsAndDisablesWindowsSearchIndexingTo,
                    Array.Empty<string>(),
                    new[] { global::OmenCore.Localization.Strings.CsWSearchService },
                    global::OmenCore.Localization.Strings.CsSetTheWindowsSearchServiceBackTo),
                "service_diagtrack" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsDisablesTheConnectedUserExperiencesAndTelemetry,
                    Array.Empty<string>(),
                    new[] { global::OmenCore.Localization.Strings.CsDiagTrackService },
                    global::OmenCore.Localization.Strings.CsSetTheDiagTrackServiceBackToAutomatic),
                "network_tcp_nodelay" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsForcesTCPToSendPacketsWithoutDelayed,
                    new[] { @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\TcpNoDelay" },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsRemoveTcpNoDelayOrSetItBackTo),
                "network_tcp_ack" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsChangesTCPAcknowledgmentBehaviorSoPacketsAre,
                    new[] { @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\TcpAckFrequency" },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsRemoveTcpAckFrequencyOrRestoreTheDefaultBehavior),
                "network_delivery_opt" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsDisablesDeliveryOptimizationPeerToPeerUpdate,
                    new[] { @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization\DODownloadMode" },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsRemoveDODownloadModeToLetWindowsManageDelivery),
                "network_nagle" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsAdjustsTCPBufferingBehaviorToMinimizeAcknowledgment,
                    new[] { @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\TCPDelAckTicks" },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsRemoveTCPDelAckTicksToRestoreTheDefaultStack),
                "input_mouse_accel" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsDisablesEnhancedPointerPrecisionSoMouseMovement,
                    new[]
                    {
                        @"HKCU\Control Panel\Mouse\MouseSpeed",
                        @"HKCU\Control Panel\Mouse\MouseThreshold1",
                        @"HKCU\Control Panel\Mouse\MouseThreshold2"
                    },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsSetMouseSpeedBackTo1AndRestore),
                "input_game_dvr" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsDisablesBackgroundGameRecordingSoGameDVR,
                    new[]
                    {
                        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled",
                        @"HKCU\System\GameConfigStore\GameDVR_Enabled",
                        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR\AllowGameDVR"
                    },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsRemoveTheGameDVRPolicyValuesOr),
                "input_game_bar" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsDisablesXboxGameBarOverlaysAndStartup,
                    new[]
                    {
                        @"HKCU\SOFTWARE\Microsoft\GameBar\UseNexusForGameBarEnabled",
                        @"HKCU\SOFTWARE\Microsoft\GameBar\ShowStartupPanel"
                    },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsRemoveThoseGameBarValuesOrRe),
                "input_fullscreen_opt" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsTunesFullscreenOptimizationBehaviorForGamesBy,
                    new[]
                    {
                        @"HKCU\System\GameConfigStore\GameDVR_FSEBehaviorMode",
                        @"HKCU\System\GameConfigStore\GameDVR_HonorUserFSEBehaviorMode",
                        @"HKCU\System\GameConfigStore\GameDVR_FSEBehavior"
                    },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsRemoveTheGameConfigStoreFullscreenValuesToRestore),
                "visual_transparency" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsDisablesAcrylicAndTransparencyEffectsToReduce,
                    new[] { @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency" },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsSetEnableTransparencyBackTo1OrRe),
                "visual_animations" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsTurnsOffCommonShellAnimationsAndMenu,
                    new[]
                    {
                        @"HKCU\Control Panel\Desktop\WindowMetrics\MinAnimate",
                        @"HKCU\Control Panel\Desktop\MenuShowDelay",
                        @"HKCU\Control Panel\Desktop\SmoothScroll"
                    },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsRestoreMinAnimateMenuShowDelayAndSmoothScrollToTheir),
                "storage_trim" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsEnablesTheNTFSTRIMPathSoSSDs,
                    Array.Empty<string>(),
                    new[] { "fsutil behavior set DisableDeleteNotify 0" },
                    global::OmenCore.Localization.Strings.CsRunFsutilBehaviorSetDisableDeleteNotify1To),
                "storage_last_access" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsDisablesNTFSLastAccessTimestampUpdatesTo,
                    Array.Empty<string>(),
                    new[] { "fsutil behavior set disablelastaccess 1" },
                    global::OmenCore.Localization.Strings.CsRunFsutilBehaviorSetDisablelastaccess0To),
                "storage_8dot3" => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsDisablesLegacy83ShortFilenameGeneration,
                    new[] { @"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\NtfsDisable8dot3NameCreation" },
                    Array.Empty<string>(),
                    global::OmenCore.Localization.Strings.CsSetNtfsDisable8dot3NameCreationBackTo0IfOlder),
                _ => new OptimizationRiskAssessment(
                    global::OmenCore.Localization.Strings.CsThisOptimizationChangesWindowsSettingsThatOmenCore,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    null)
            };
        }
    }

    /// <summary>
    /// Represents a single optimization toggle item in the UI.
    /// </summary>
    public class OptimizationItem : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private bool _isApplying;
        private bool _isDetailsExpanded;

        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public OptimizationRisk Risk { get; set; }
        public string? Warning { get; set; }
        public string? DetailedDescription { get; set; }
        public IReadOnlyList<string> AffectedRegistryKeys { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> ServiceNamesAffected { get; set; } = Array.Empty<string>();
        public string? UndoInstructions { get; set; }
        
        public Func<OptimizationItem, bool, Task>? OnToggleAsync { get; set; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public bool IsApplying
        {
            get => _isApplying;
            set { _isApplying = value; OnPropertyChanged(); }
        }

        public bool IsDetailsExpanded
        {
            get => _isDetailsExpanded;
            set { _isDetailsExpanded = value; OnPropertyChanged(); }
        }

        public string RiskText => Risk switch
        {
            OptimizationRisk.Low => global::OmenCore.Localization.Strings.CsLowRisk,
            OptimizationRisk.Medium => global::OmenCore.Localization.Strings.CsMediumRisk,
            OptimizationRisk.High => global::OmenCore.Localization.Strings.CsHighRisk,
            _ => "Unknown"
        };

        public string RiskColor => Risk switch
        {
            OptimizationRisk.Low => "#00C8C8",
            OptimizationRisk.Medium => "#FFB800",
            OptimizationRisk.High => "#FF005C",
            _ => "#808080"
        };

        public bool HasWarning => !string.IsNullOrEmpty(Warning);
        public bool HasDetailedDescription => !string.IsNullOrWhiteSpace(DetailedDescription);
        public bool HasRegistryPreview => AffectedRegistryKeys.Count > 0;
        public bool HasServicePreview => ServiceNamesAffected.Count > 0;
        public bool HasUndoInstructions => !string.IsNullOrWhiteSpace(UndoInstructions);
        public bool HasDetails => HasDetailedDescription || HasRegistryPreview || HasServicePreview || HasUndoInstructions;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public async Task Toggle(bool desiredState)
        {
            try
            {
                if (OnToggleAsync != null)
                {
                    await OnToggleAsync(this, desiredState);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Toggle failed: {ex.Message}");
            }
        }
    }

    public sealed record OptimizationRiskAssessment(
        string DetailedDescription,
        IReadOnlyList<string> AffectedRegistryKeys,
        IReadOnlyList<string> ServiceNamesAffected,
        string? UndoInstructions);
}
