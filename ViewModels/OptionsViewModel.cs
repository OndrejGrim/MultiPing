using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiPing.Models;
using MultiPing.Services;

namespace MultiPing.ViewModels;

/// <summary>
/// ViewModel for the Options dialog. Exposes editable application settings and coordinates
/// persistence and updating the active window's state upon save/apply.
/// </summary>
public partial class OptionsViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly ConfigService _configSvc;
    private readonly MonitorViewModelBase? _activeMonitor;

    [ObservableProperty] private int _pingIntervalSeconds;
    [ObservableProperty] private int _pingTimeoutMs;
    [ObservableProperty] private int _maxHops;
    [ObservableProperty] private int _lookAheadLimit;
    [ObservableProperty] private double _sampleWindowMinutes;
    [ObservableProperty] private SampleWindowOption? _selectedSampleWindow;
    [ObservableProperty] private bool _logByDefault;
    [ObservableProperty] private bool _rememberPlotSelection;
    [ObservableProperty] private bool _autoStartMultiPing;
    [ObservableProperty] private string _logDirectory = string.Empty;
    [ObservableProperty] private AppModeOption _selectedDefaultMode;

    public ObservableCollection<SampleWindowOption> SampleWindowOptions { get; } = new(SampleWindowOption.Presets);

    public IReadOnlyList<AppModeOption> DefaultModeOptions { get; } = AppModeOption.All;

    public Func<Task<string?>>? PickFolderHandler { get; set; }
    public Action? CloseAction { get; set; }

    public OptionsViewModel(AppConfig config, ConfigService configSvc, MonitorViewModelBase? activeMonitor = null)
    {
        _config = config;
        _configSvc = configSvc;
        _activeMonitor = activeMonitor;

        // Populate with current configuration values
        _pingIntervalSeconds = Math.Max(1, config.General.PingIntervalMs / 1000);
        _pingTimeoutMs = config.General.PingTimeoutMs;
        _maxHops = config.Traceroute.MaxHops;
        _lookAheadLimit = config.Traceroute.LookAheadLimit;
        _sampleWindowMinutes = Math.Max(1, config.General.SampleWindowMinutes);
        _selectedSampleWindow = GetOrCreateSampleWindowOption(_sampleWindowMinutes);
        _logByDefault = config.Logging.LogByDefault;
        _rememberPlotSelection = config.MultiPing.RememberPlotSelection;
        _autoStartMultiPing = config.MultiPing.AutoStart;
        _logDirectory = config.Logging.LogDirectory ?? string.Empty;
        _selectedDefaultMode = AppModeOption.For(config.General.DefaultMode);
    }

    partial void OnSampleWindowMinutesChanged(double value)
    {
        if (SelectedSampleWindow is null || Math.Abs(SelectedSampleWindow.Minutes - value) > 0.0001)
            SelectedSampleWindow = GetOrCreateSampleWindowOption(value);
    }

    partial void OnSelectedSampleWindowChanged(SampleWindowOption? value)
    {
        if (value is not null)
            SampleWindowMinutes = value.Minutes;
    }

    private SampleWindowOption GetOrCreateSampleWindowOption(double minutes)
    {
        SampleWindowOption? existing = SampleWindowOptions.FirstOrDefault(option =>
            Math.Abs(option.Minutes - minutes) < 0.0001);
        if (existing is not null)
            return existing;

        existing = SampleWindowOption.ForMinutes(minutes);
        SampleWindowOptions.Add(existing);
        return existing;
    }

    [RelayCommand]
    private async Task BrowseLogDirectoryAsync()
    {
        if (PickFolderHandler is not null)
        {
            string? folder = await PickFolderHandler();
            if (!string.IsNullOrEmpty(folder))
            {
                LogDirectory = folder;
            }
        }
    }

    [RelayCommand]
    private void ResetLogDirectory()
    {
        LogDirectory = string.Empty;
    }

    [RelayCommand]
    public void Apply()
    {
        _config.General.DefaultMode = SelectedDefaultMode.Mode;
        _config.General.PingIntervalMs = Math.Max(1, PingIntervalSeconds) * 1000;
        _config.General.SampleWindowMinutes = Math.Max(1, SampleWindowMinutes);
        _config.General.PingTimeoutMs = Math.Max(100, PingTimeoutMs);

        _config.MultiPing.RememberPlotSelection = RememberPlotSelection;
        _config.MultiPing.AutoStart = AutoStartMultiPing;

        _config.Traceroute.MaxHops = Math.Clamp(MaxHops, 1, 128);
        _config.Traceroute.LookAheadLimit = Math.Clamp(LookAheadLimit, 1, 5);

        _config.Logging.LogByDefault = LogByDefault;
        _config.Logging.LogDirectory = LogDirectory.Trim();

        _configSvc.Save(_config);

        if (_activeMonitor is not null)
        {
            _activeMonitor.PingIntervalMs = _config.General.PingIntervalMs;
            _activeMonitor.SampleWindowMinutes = _config.General.SampleWindowMinutes;
            _activeMonitor.LogByDefault = _config.Logging.LogByDefault;
        }
    }

    [RelayCommand]
    public void Ok()
    {
        Apply();
        CloseAction?.Invoke();
    }

    [RelayCommand]
    public void Cancel()
    {
        CloseAction?.Invoke();
    }
}
