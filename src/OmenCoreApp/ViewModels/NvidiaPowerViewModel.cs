using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using OmenCore.Services;
using OmenCore.Utils;

namespace OmenCore.ViewModels
{
    /// <summary>
    /// Tuning panel for the opt-in NVIDIA laptop GPU power unlock. Nothing is read or written until the user
    /// turns it on, and every write is confirmed first.
    /// </summary>
    public class NvidiaPowerViewModel : INotifyPropertyChanged
    {
        private readonly ConfigurationService _config;
        private readonly NvidiaPowerService _service;
        private readonly LoggingService _logging;
        private NvidiaPowerService.Status? _status;
        private bool _busy;
        private string _statusText = "";
        private string _resultText = "";
        private int _selectedTarget;

        public NvidiaPowerViewModel(ConfigurationService config, NvidiaPowerService service, LoggingService logging)
        {
            _config = config;
            _service = service;
            _logging = logging;
            RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => Enabled && !_busy);
            ApplyMaxCommand = new AsyncRelayCommand(_ => RunAsync("MAX", w => _service.ApplyMax(w), true),
                _ => Enabled && !_busy && SelectedTarget > 0 && _status?.MaxReady == true);
            ApplyCurrentCommand = new AsyncRelayCommand(_ => RunAsync("CURRENT", w => _service.ApplyCurrent(w), false),
                _ => Enabled && !_busy && SelectedTarget > 0 && _status?.CurrentReady == true);
            RemoveMaxCommand = new AsyncRelayCommand(_ => RunAsync(global::OmenCore.Localization.Strings.CsRemoveMAXOverride, _ => _service.RemoveMax(), true),
                _ => Enabled && !_busy && _status?.MaxReady == true);
            ResolveVbiosCommand = new AsyncRelayCommand(_ => ResolveAsync(null), _ => Enabled && !_busy);
            ValidateDriverCommand = new AsyncRelayCommand(_ => ValidateDriverAsync(), _ => Enabled && !_busy && NeedsDriverValidation);
            ResolveVbiosFromFileCommand = new AsyncRelayCommand(_ =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = global::OmenCore.Localization.Strings.CsSelectAVBIOSDump, Filter = "VBIOS dump (*.rom;*.bin)|*.rom;*.bin|All files|*.*" };
                return dlg.ShowDialog() == true ? ResolveAsync(dlg.FileName) : Task.CompletedTask;
            }, _ => Enabled && !_busy);
            if (Enabled) _ = RefreshAsync();
        }

        public ICommand ResolveVbiosCommand { get; }
        public ICommand ValidateDriverCommand { get; }

        /// <summary>True when the installed NVIDIA driver is not the reviewed one and has not been validated yet.</summary>
        public bool NeedsDriverValidation => _status?.NeedsDriverValidation == true;

        private async Task ValidateDriverAsync()
        {
            const string prompt = "Validate this NVIDIA driver for CURRENT writes?\n\nOmenCore sends one request that repeats the " +
                "driver's own current power request, then checks that nothing changed. Close games and GPU-heavy apps first. " +
                "If anything does not match, writes stay locked.";
            if (MessageBox.Show(prompt, "NVIDIA power", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

            _busy = true; RaiseCommands();
            try
            {
                var r = await Task.Run(() => _service.ValidateDriver());
                ResultText = (r.Success ? "Done: " : "Not validated: ") + r.Message;
                _logging.Info("NVIDIA power panel: " + ResultText);
            }
            finally
            {
                _busy = false;
                await RefreshAsync();
            }
        }
        public ICommand ResolveVbiosFromFileCommand { get; }

        private async Task ResolveAsync(string? romPath)
        {
            _busy = true; RaiseCommands();
            try
            {
                var r = await Task.Run(() => romPath == null ? _service.ResolveVbiosAuto() : _service.ResolveVbiosFromRom(romPath));
                ResultText = (r.Success ? "Done: " : global::OmenCore.Localization.Strings.CsNotResolved) + r.Message;
            }
            finally
            {
                _busy = false;
                await RefreshAsync();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<int> Targets { get; } = new();
        public ICommand RefreshCommand { get; }
        public ICommand ApplyMaxCommand { get; }
        public ICommand ApplyCurrentCommand { get; }
        public ICommand RemoveMaxCommand { get; }

        public bool Enabled
        {
            get => _config.Config.Features?.NvidiaPowerUnlockEnabled == true;
            set
            {
                if (Enabled == value) return;
                _config.Config.Features ??= new OmenCore.Models.FeaturePreferences();
                _config.Config.Features.NvidiaPowerUnlockEnabled = value;
                _config.Save(_config.Config);
                OnPropertyChanged();
                if (value) _ = RefreshAsync(); else StatusText = "";
                RaiseCommands();
            }
        }

        public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }
        public string ResultText { get => _resultText; private set { _resultText = value; OnPropertyChanged(); } }

        public int SelectedTarget
        {
            get => _selectedTarget;
            set { _selectedTarget = value; OnPropertyChanged(); RaiseCommands(); }
        }

        private Task RefreshAsync() => Task.Run(() =>
        {
            NvidiaPowerService.Status s;
            try { s = _service.Assess(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException)
            {
                Post(() => StatusText = global::OmenCore.Localization.Strings.CsCouldNotReadTheNVIDIADriver + ex.Message);
                return;
            }
            Post(() =>
            {
                _status = s;
                Targets.Clear();
                foreach (var t in s.Targets) Targets.Add(t);
                if (!Targets.Contains(SelectedTarget)) SelectedTarget = Targets.FirstOrDefault();
                var live = s.LiveCurrentW.HasValue && s.LiveMaxW.HasValue
                    ? string.Format(global::OmenCore.Localization.Strings.CsLiveCURRENTWLiveMAXW, s.LiveCurrentW, s.LiveMaxW) : "";
                StatusText = $"{(string.IsNullOrEmpty(s.Gpu) ? "NVIDIA GPU" : s.Gpu)}: {live}" +
                             string.Format(global::OmenCore.Localization.Strings.CsMAXWritesCURRENTWrites, (s.MaxReady ? "ready" : "not ready"), (s.CurrentReady ? "ready" : "not ready"), s.Reason) +
                             StockMismatchNote(s.LiveMaxW, s.ExpectedStockW);
                OnPropertyChanged(nameof(NeedsDriverValidation));
                RaiseCommands();
            });
        });

        private async Task RunAsync(string what, Func<int, NvidiaPowerService.Outcome> op, bool mayNeedReboot)
        {
            var watts = SelectedTarget;
            var prompt = what.StartsWith("remove", StringComparison.Ordinal)
                ? global::OmenCore.Localization.Strings.CsRemoveTheMAXOverrideWindowsMustBe
                : string.Format(global::OmenCore.Localization.Strings.CsApplyWThisChangesTheGPUS, what, watts) +
                  global::OmenCore.Localization.Strings.CsHigherPowerMeansMoreHeatAndThe +
                  (mayNeedReboot ? global::OmenCore.Localization.Strings.CsMAXNeedsAWindowsRestartToTake : global::OmenCore.Localization.Strings.CsCURRENTResetsOnReboot) +
                  global::OmenCore.Localization.Strings.CsOnlyContinueIfYouUnderstandTheRisk;
            if (MessageBox.Show(prompt, global::OmenCore.Localization.Strings.CsNVIDIAPower, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

            _busy = true; RaiseCommands();
            try
            {
                var r = await Task.Run(() => op(watts));
                ResultText = (r.Success ? "Done: " : global::OmenCore.Localization.Strings.CsNotApplied) + r.Message + (r.RebootRequired ? global::OmenCore.Localization.Strings.CsRestartWindowsToFinish : "");
                _logging.Info("NVIDIA power panel: " + ResultText);
            }
            finally
            {
                _busy = false;
                await RefreshAsync();
            }
        }

        /// <summary>
        /// When the live MAX read from the driver differs from the stock MAX we expect for this GPU (the 115 W against 140 W case),
        /// say so and say what it can mean. Read only; it never changes what is written. Empty when they agree or either is unknown.
        /// </summary>
        internal static string StockMismatchNote(double? liveMaxW, int? expectedStockW)
        {
            if (!liveMaxW.HasValue || !expectedStockW.HasValue || Math.Abs(liveMaxW.Value - expectedStockW.Value) <= 1.0)
                return string.Empty;

            return string.Format(global::OmenCore.Localization.Strings.CsStockMaxReadWExpectedWA, liveMaxW.Value, expectedStockW.Value) +
                   global::OmenCore.Localization.Strings.CsIsInstalledOrTheLaptopMakerSet;
        }

        private void RaiseCommands()
        {
            foreach (var c in new[] { RefreshCommand, ApplyMaxCommand, ApplyCurrentCommand, RemoveMaxCommand, ResolveVbiosCommand, ResolveVbiosFromFileCommand, ValidateDriverCommand })
                (c as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private static void Post(Action a)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) a(); else d.BeginInvoke(a);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
