using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using FocusFence.Models;
using FocusFence.Windows;

namespace FocusFence.Services;

public class ZoneManagerService
{
    private readonly AppConfig _config;
    private readonly List<ZoneWindow> _zones = new();
    private bool _zonesVisible = true;

    // Events to communicate back to App/Dashboard
    public event Action? RequestSave;
    public event Action? RequestDashboardRefresh;
    public event Action<string?>? RequestStartPomodoro;
    public event Action<string>? RequestContextLaunch;

    public IReadOnlyList<ZoneWindow> Zones => _zones;
    public bool ZonesVisible => _zonesVisible;

    public ZoneManagerService(AppConfig config)
    {
        _config = config;
    }

    public void InitializeInitialZones()
    {
        foreach (var zc in _config.Zones.ToList())
        {
            CreateZoneWindow(zc);
        }
    }

    public void CreateZoneWindow(ZoneConfig zc)
    {
        if (zc.Width < 100) zc.Width = 320;
        if (zc.Height < 50) zc.Height = 280;

        var zone = new ZoneWindow(zc);
        _zones.Add(zone);
        
        zone.ZoneChanged += () =>
        {
            RequestSave?.Invoke();
            RequestDashboardRefresh?.Invoke();
        };
        
        zone.ZoneCloseRequested += (z) => 
        {
            z.HideZone();
            RequestDashboardRefresh?.Invoke();
            RequestSave?.Invoke();
        };

        zone.PomodoroStartRequested += (zoneId) => RequestStartPomodoro?.Invoke(zoneId);
        zone.ContextLaunchRequested += (zoneId) => RequestContextLaunch?.Invoke(zoneId);

        bool isOnPage = zc.IsPinned || zc.DesktopPage == _config.ActiveDesktopPage;
        if (zc.IsVisible && isOnPage)
        {
            zone.Show();
        }
    }

    public void OnCreateZoneRequest(ZoneConfig newConfig)
    {
        _config.Zones.Add(newConfig);
        
        if (!string.IsNullOrEmpty(newConfig.FolderPath))
            Directory.CreateDirectory(newConfig.FolderPath);

        CreateZoneWindow(newConfig);
        
        var newZone = _zones.LastOrDefault(z => z.Config == newConfig);
        if (newZone != null)
        {
            newZone.Activate();
            newZone.Topmost = true;
            newZone.Topmost = false;
        }

        RequestSave?.Invoke();
    }

    public void OnDeleteZoneRequest(ZoneConfig config)
    {
        _config.Zones.Remove(config);
        var zone = _zones.FirstOrDefault(z => z.Config == config);
        if (zone != null)
        {
            _zones.Remove(zone);
            zone.Close();
        }
        RequestSave?.Invoke();
    }

    public void OnToggleZoneRequest(ZoneConfig config, bool isVisible)
    {
        var zone = _zones.FirstOrDefault(z => z.Config == config);
        if (zone != null)
        {
            if (isVisible) zone.ShowZone();
            else zone.HideZone();
        }
    }

    public void SummonZone(ZoneConfig config)
    {
        var zone = _zones.FirstOrDefault(z => z.Config == config);
        if (zone != null)
        {
            zone.ShowZone();
            zone.Topmost = true;
            zone.Activate();
            if (!config.IsPinned)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer.Tick += (_, _) => { timer.Stop(); zone.Topmost = false; };
                timer.Start();
            }
        }
    }

    public void ToggleAllZones()
    {
        _zonesVisible = !_zonesVisible;

        if (!_zonesVisible && _config.Zones.Any(z => z.IsActiveContext))
        {
            foreach (var zone in _zones)
            {
                if (zone.Config.IsActiveContext)
                {
                    bool isOnPage = zone.Config.IsPinned || zone.Config.DesktopPage == _config.ActiveDesktopPage;
                    if (isOnPage && zone.Config.IsVisible)
                        zone.AnimateIn();
                }
                else
                {
                    zone.AnimateOut();
                }
            }
        }
        else
        {
            UpdateZoneVisibilityForPage();
        }
    }

    public void UpdateZoneVisibilityForPage()
    {
        foreach (var zone in _zones)
        {
            if (!_zonesVisible)
            {
                zone.Visibility = Visibility.Hidden;
                continue;
            }

            bool isOnPage = zone.Config.IsPinned || zone.Config.DesktopPage == _config.ActiveDesktopPage;
            if (isOnPage && zone.Config.IsVisible)
                zone.ShowZone();
            else
                zone.Visibility = Visibility.Hidden;
        }
    }

    public void AutoArrangeZones(DashboardWindow dashboard)
    {
        var visibleZones = _zones
            .Where(z => z.IsVisible && z.Config.IsVisible)
            .OrderBy(z => _config.Zones.IndexOf(z.Config))
            .ToList();
        if (visibleZones.Count == 0) return;

        var handle = new System.Windows.Interop.WindowInteropHelper(dashboard).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(handle);

        var source = System.Windows.PresentationSource.FromVisual(dashboard);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        double screenWidth = screen.WorkingArea.Width / scaleX;
        double screenHeight = screen.WorkingArea.Height / scaleY;
        
        double gap = 24;
        double startX = (screen.WorkingArea.Left / scaleX) + gap;
        double startY = (screen.WorkingArea.Top / scaleY) + gap;
        
        double currentX = startX;
        double currentY = startY;
        double rowMaxHeight = 0;

        foreach (var zone in visibleZones)
        {
            if (currentX + zone.Width > screenWidth && currentX > startX)
            {
                currentX = startX;
                currentY += rowMaxHeight + gap;
                rowMaxHeight = 0;
            }

            var leftAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = currentX,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            var topAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = currentY,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            zone.BeginAnimation(Window.LeftProperty, leftAnim);
            zone.BeginAnimation(Window.TopProperty, topAnim);

            zone.Config.X = currentX;
            zone.Config.Y = currentY;

            currentX += zone.Width + gap;
            if (zone.Height > rowMaxHeight) rowMaxHeight = zone.Height;
        }
        
        ConfigService.Save(_config);
    }

    public void CloseAll()
    {
        foreach (var zone in _zones) zone.Close();
        _zones.Clear();
    }

    public void SnapshotAllPositions()
    {
        foreach (var zone in _zones) zone.SnapshotPosition();
    }

    public void SyncFromConfig()
    {
        foreach (var zone in _zones) zone.SyncFromConfig();
    }

    public ZoneWindow? GetZone(string zoneId)
    {
        return _zones.FirstOrDefault(z => z.Config.Id == zoneId);
    }
}
