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
        _pingIntervalSeconds = Math.Max(1, config.PingIntervalMs / 1000);
        _pingTimeoutMs = config.PingTimeoutMs;
        _maxHops = config.MaxHops;
        _lookAheadLimit = config.LookAheadLimit;
        _sampleWindowMinutes = Math.Max(1, config.SampleWindowMinutes);
        _selectedSampleWindow = GetOrCreateSampleWindowOption(_sampleWindowMinutes);
        _logByDefault = config.LogByDefault;
        _rememberPlotSelection = config.RememberPlotSelection;
        _logDirectory = config.LogDirectory ?? string.Empty;
        _selectedDefaultMode = AppModeOption.For(config.DefaultMode);
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
        _config.PingIntervalMs = Math.Max(1, PingIntervalSeconds) * 1000;
        _config.PingTimeoutMs = Math.Max(100, PingTimeoutMs);
        _config.MaxHops = Math.Clamp(MaxHops, 1, 128);
        _config.LookAheadLimit = Math.Clamp(LookAheadLimit, 1, 5);
        _config.SampleWindowMinutes = Math.Max(1, SampleWindowMinutes);
        _config.LogByDefault = LogByDefault;
        _config.RememberPlotSelection = RememberPlotSelection;
        _config.LogDirectory = LogDirectory.Trim();
        _config.DefaultMode = SelectedDefaultMode.Mode;

        _configSvc.Save(_config);

        if (_activeMonitor is not null)
        {
            _activeMonitor.PingIntervalMs = _config.PingIntervalMs;
            _activeMonitor.SampleWindowMinutes = _config.SampleWindowMinutes;
            _activeMonitor.LogByDefault = _config.LogByDefault;
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
