using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RenoDXCommander.Models;
using RenoDXCommander.Services;
using RenoDXCommander.ViewModels;

namespace RenoDXCommander;

public partial class SettingsHandler
{
    // ── Hotkey UI event handlers (placeholder — implemented in Tasks 6.2 / 6.3) ──

    /// <summary>
    /// Handles PreviewKeyDown on the HotkeyBox to capture key combinations.
    /// Full implementation in Task 6.2.
    /// </summary>
    public void HotkeyBox_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var key = e.Key;

        // Req 7.4: Ignore modifier-only keys — do not update display when only a modifier is pressed
        if (key == Windows.System.VirtualKey.Control ||
            key == Windows.System.VirtualKey.Shift ||
            key == Windows.System.VirtualKey.Menu ||       // Alt
            key == (Windows.System.VirtualKey)91 ||        // Left Windows
            key == (Windows.System.VirtualKey)92 ||        // Right Windows
            key == Windows.System.VirtualKey.LeftControl ||
            key == Windows.System.VirtualKey.RightControl ||
            key == Windows.System.VirtualKey.LeftShift ||
            key == Windows.System.VirtualKey.RightShift ||
            key == Windows.System.VirtualKey.LeftMenu ||
            key == Windows.System.VirtualKey.RightMenu)
        {
            return;
        }

        // Extract VK code from the pressed key (Req 2.2)
        int vk = (int)key;

        // Read modifier state from the current keyboard state (Req 2.1)
        bool shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // Build and store the hotkey string in KeyOverlay format (Req 2.2)
        _currentHotkeyString = HotkeyManager.BuildScreenshotHotkeyString(vk, shift, ctrl, alt);

        // Update the TextBox display with human-readable format (Req 2.1)
        if (sender is TextBox hotkeyBox)
        {
            hotkeyBox.Text = HotkeyManager.FormatHotkeyDisplay(_currentHotkeyString);
        }

        // Prevent the TextBox from receiving the character
        e.Handled = true;
    }

    /// <summary>
    /// Handles the Apply to All Games button click for the overlay hotkey.
    /// Persists the hotkey, applies it to all managed reshade*.ini files, and shows a confirmation dialog.
    /// </summary>
    public async void ApplyOverlayHotkey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Req 4.6: Persist hotkey to SettingsViewModel before iterating game cards
            ViewModel.Settings.OverlayHotkey = _currentHotkeyString;
            ViewModel.SaveSettingsPublic();

            bool isDefault = HotkeyManager.IsDefaultHotkey(_currentHotkeyString);

            // Req 4.1: Iterate all game cards with a non-empty InstallPath
            int updatedCount = 0;
            foreach (var card in ViewModel.AllCards)
            {
                if (string.IsNullOrEmpty(card.InstallPath)) continue;
                if (!System.IO.Directory.Exists(card.InstallPath)) continue;

                // When the hotkey is the default (Home), skip RDR2 — its template uses
                // END and we don't want to overwrite that with the generic default.
                if (isDefault && AuxInstallService.IsRdr2(card.GameName))
                    continue;

                // Req 4.2: Locate all reshade*.ini files in the game's install directory
                var iniFiles = System.IO.Directory.EnumerateFiles(card.InstallPath, "reshade*.ini")
                    .Where(f => System.IO.Path.GetFileName(f).StartsWith("reshade", StringComparison.OrdinalIgnoreCase)
                             && System.IO.Path.GetExtension(f).Equals(".ini", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (iniFiles.Count == 0) continue;

                try
                {
                    // Req 4.3: Write KeyOverlay to [INPUT] section of each reshade*.ini
                    foreach (var iniFile in iniFiles)
                    {
                        AuxInstallService.ApplyOverlayHotkey(iniFile, _currentHotkeyString);
                    }
                    updatedCount++;
                }
                catch (System.IO.IOException ex)
                {
                    CrashReporter.Log($"[SettingsHandler.ApplyOverlayHotkey_Click] Failed for '{card.GameName}' — {ex.Message}");
                }
            }

            // Req 4.5: Show confirmation dialog with count of updated files
            var dialog = new ContentDialog
            {
                Title = "ReShade UI Hotkey",
                Content = $"Updated {updatedCount} reshade.ini file{(updatedCount == 1 ? "" : "s")}.",
                CloseButtonText = "OK",
                XamlRoot = _window.Content.XamlRoot,
                RequestedTheme = ElementTheme.Dark,
            };
            await DialogService.ShowSafeAsync(dialog);
        }
        catch (Exception unhandledEx)
        {
            CrashReporter.Log($"[SettingsHandler.ApplyOverlayHotkey_Click] Unhandled error — {unhandledEx.Message}\n{unhandledEx.StackTrace}");
        }
    }

    // ── Combined ReShade Hotkeys (overlay + screenshot) ───────────────────────

    /// <summary>
    /// Applies both the overlay hotkey and screenshot hotkey to all managed reshade*.ini files.
    /// </summary>
    public async void ApplyReShadeHotkeys_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.Settings.OverlayHotkey = _currentHotkeyString;
            ViewModel.Settings.ScreenshotHotkey = _currentScreenshotHotkeyString;
            ViewModel.SaveSettingsPublic();

            bool isDefault = HotkeyManager.IsDefaultHotkey(_currentHotkeyString);

            int updatedCount = 0;
            foreach (var card in ViewModel.AllCards)
            {
                if (string.IsNullOrEmpty(card.InstallPath)) continue;
                if (!System.IO.Directory.Exists(card.InstallPath)) continue;

                if (isDefault && AuxInstallService.IsRdr2(card.GameName))
                    continue;

                var iniFiles = System.IO.Directory.EnumerateFiles(card.InstallPath, "reshade*.ini")
                    .Where(f => System.IO.Path.GetFileName(f).StartsWith("reshade", StringComparison.OrdinalIgnoreCase)
                             && System.IO.Path.GetExtension(f).Equals(".ini", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (iniFiles.Count == 0) continue;

                try
                {
                    foreach (var iniFile in iniFiles)
                    {
                        AuxInstallService.ApplyOverlayHotkey(iniFile, _currentHotkeyString);
                        AuxInstallService.ApplyScreenshotHotkey(iniFile, _currentScreenshotHotkeyString);
                    }
                    updatedCount++;
                }
                catch (Exception ex)
                {
                    CrashReporter.Log($"[SettingsHandler.ApplyReShadeHotkeys_Click] Failed for '{card.GameName}' — {ex.Message}");
                }
            }

            var dialog = new ContentDialog
            {
                Title = "ReShade Hotkeys",
                Content = $"Updated {updatedCount} reshade.ini file{(updatedCount == 1 ? "" : "s")}.",
                CloseButtonText = "OK",
                XamlRoot = _window.Content.XamlRoot,
                RequestedTheme = ElementTheme.Dark,
            };
            await DialogService.ShowSafeAsync(dialog);
        }
        catch (Exception unhandledEx)
        {
            CrashReporter.Log($"[SettingsHandler.ApplyReShadeHotkeys_Click] Unhandled error — {unhandledEx.Message}\n{unhandledEx.StackTrace}");
        }
    }

    // ── Screenshot Hotkey ─────────────────────────────────────────────────────

    /// <summary>
    /// Handles PreviewKeyDown on the ScreenshotHotkeyBox to capture key combinations.
    /// </summary>
    public void ScreenshotHotkeyBox_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var key = e.Key;

        if (key == Windows.System.VirtualKey.Control ||
            key == Windows.System.VirtualKey.Shift ||
            key == Windows.System.VirtualKey.Menu ||
            key == (Windows.System.VirtualKey)91 ||
            key == (Windows.System.VirtualKey)92 ||
            key == Windows.System.VirtualKey.LeftControl ||
            key == Windows.System.VirtualKey.RightControl ||
            key == Windows.System.VirtualKey.LeftShift ||
            key == Windows.System.VirtualKey.RightShift ||
            key == Windows.System.VirtualKey.LeftMenu ||
            key == Windows.System.VirtualKey.RightMenu)
        {
            return;
        }

        int vk = (int)key;

        bool shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        _currentScreenshotHotkeyString = HotkeyManager.BuildScreenshotHotkeyString(vk, shift, ctrl, alt);

        if (sender is TextBox hotkeyBox)
        {
            hotkeyBox.Text = HotkeyManager.FormatHotkeyDisplay(_currentScreenshotHotkeyString);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Handles the Apply to All Games button click for the screenshot hotkey.
    /// Persists the hotkey, applies it to all managed reshade*.ini files, and shows a confirmation dialog.
    /// </summary>
    public async void ApplyScreenshotHotkey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.Settings.ScreenshotHotkey = _currentScreenshotHotkeyString;
            ViewModel.SaveSettingsPublic();

            int updatedCount = 0;
            foreach (var card in ViewModel.AllCards)
            {
                if (string.IsNullOrEmpty(card.InstallPath)) continue;
                if (!System.IO.Directory.Exists(card.InstallPath)) continue;

                var iniFiles = System.IO.Directory.EnumerateFiles(card.InstallPath, "reshade*.ini")
                    .Where(f => System.IO.Path.GetFileName(f).StartsWith("reshade", StringComparison.OrdinalIgnoreCase)
                             && System.IO.Path.GetExtension(f).Equals(".ini", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (iniFiles.Count == 0) continue;

                try
                {
                    foreach (var iniFile in iniFiles)
                    {
                        AuxInstallService.ApplyScreenshotHotkey(iniFile, _currentScreenshotHotkeyString);
                    }
                    updatedCount++;
                }
                catch (Exception ex)
                {
                    CrashReporter.Log($"[SettingsHandler.ApplyScreenshotHotkey_Click] Failed for '{card.GameName}' — {ex.Message}");
                }
            }

            var dialog = new ContentDialog
            {
                Title = "ReShade Screenshot Hotkey",
                Content = $"Updated {updatedCount} reshade.ini file{(updatedCount == 1 ? "" : "s")}.",
                CloseButtonText = "OK",
                XamlRoot = _window.Content.XamlRoot,
                RequestedTheme = ElementTheme.Dark,
            };
            await DialogService.ShowSafeAsync(dialog);
        }
        catch (Exception unhandledEx)
        {
            CrashReporter.Log($"[SettingsHandler.ApplyScreenshotHotkey_Click] Unhandled error — {unhandledEx.Message}\n{unhandledEx.StackTrace}");
        }
    }

    /// <summary>Resets the screenshot hotkey to the default Print Screen key.</summary>
    public void ResetScreenshotHotkey_Click(object sender, RoutedEventArgs e)
    {
        _currentScreenshotHotkeyString = "44,0,0,0";
        _window.ScreenshotHotkeyBox.Text = HotkeyManager.FormatHotkeyDisplay("44,0,0,0");
        ViewModel.Settings.ScreenshotHotkey = "44,0,0,0";
        ViewModel.SaveSettingsPublic();
    }

    // ── ReLimiter OSD Hotkey ──────────────────────────────────────────────────

    public void UlHotkeyBox_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var key = e.Key;

        if (key == Windows.System.VirtualKey.Control ||
            key == Windows.System.VirtualKey.Shift ||
            key == Windows.System.VirtualKey.Menu ||
            key == (Windows.System.VirtualKey)91 ||
            key == (Windows.System.VirtualKey)92 ||
            key == Windows.System.VirtualKey.LeftControl ||
            key == Windows.System.VirtualKey.RightControl ||
            key == Windows.System.VirtualKey.LeftShift ||
            key == Windows.System.VirtualKey.RightShift ||
            key == Windows.System.VirtualKey.LeftMenu ||
            key == Windows.System.VirtualKey.RightMenu)
        {
            return;
        }

        int vk = (int)key;

        bool shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        _currentUlHotkeyString = HotkeyManager.BuildUlHotkeyString(vk, shift, ctrl, alt);

        if (sender is TextBox hotkeyBox)
            hotkeyBox.Text = _currentUlHotkeyString;

        e.Handled = true;
    }

    public async void ApplyUlOsdHotkey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.Settings.UlOsdHotkey = _currentUlHotkeyString;
            ViewModel.SaveSettingsPublic();

            bool sharedPresets = ViewModel.Settings.UlSharedPresets;
            bool dlssHooks = ViewModel.Settings.UlDlssHooks;
            int targetFps = ViewModel.Settings.UlTargetFps;
            int updatedCount = 0;
            foreach (var card in ViewModel.AllCards)
            {
                if (string.IsNullOrEmpty(card.InstallPath)) continue;

                var deployPath = ModInstallService.GetAddonDeployPath(card.InstallPath);
                var iniFile = Path.Combine(deployPath, "relimiter.ini");
                if (!File.Exists(iniFile)) continue;

                try
                {
                    AuxInstallService.ApplyUlOsdHotkey(iniFile, _currentUlHotkeyString);
                    AuxInstallService.ApplyUlSharedPresets(iniFile, sharedPresets);
                    AuxInstallService.ApplyUlDlssHooks(iniFile, dlssHooks);
                    AuxInstallService.ApplyUlTargetFps(iniFile, targetFps);

                    // RE Framework games also store relimiter.ini in _storage_
                    if (card.IsRefInstalled)
                    {
                        var storagePath = Path.Combine(card.InstallPath, "_storage_", "relimiter.ini");
                        if (File.Exists(storagePath))
                        {
                            AuxInstallService.ApplyUlOsdHotkey(storagePath, _currentUlHotkeyString);
                            AuxInstallService.ApplyUlSharedPresets(storagePath, sharedPresets);
                            AuxInstallService.ApplyUlDlssHooks(storagePath, dlssHooks);
                            AuxInstallService.ApplyUlTargetFps(storagePath, targetFps);
                        }
                    }

                    updatedCount++;
                }
                catch (Exception ex)
                {
                    CrashReporter.Log($"[SettingsHandler.ApplyUlOsdHotkey_Click] Failed for '{card.GameName}' — {ex.Message}");
                }
            }

            // Also update the template in AppData
            if (File.Exists(AuxInstallService.UlIniPath))
            {
                try
                {
                    AuxInstallService.ApplyUlOsdHotkey(AuxInstallService.UlIniPath, _currentUlHotkeyString);
                    AuxInstallService.ApplyUlSharedPresets(AuxInstallService.UlIniPath, sharedPresets);
                    AuxInstallService.ApplyUlDlssHooks(AuxInstallService.UlIniPath, dlssHooks);
                    AuxInstallService.ApplyUlTargetFps(AuxInstallService.UlIniPath, targetFps);
                }
                catch (Exception ex)
                {
                    CrashReporter.Log($"[SettingsHandler.ApplyUlOsdHotkey_Click] Failed to update template — {ex.Message}");
                }
            }

            var dialog = new ContentDialog
            {
                Title = "ReLimiter Settings",
                Content = $"Updated {updatedCount} relimiter.ini file{(updatedCount == 1 ? "" : "s")}.",
                CloseButtonText = "OK",
                XamlRoot = _window.Content.XamlRoot,
                RequestedTheme = ElementTheme.Dark,
            };
            await DialogService.ShowSafeAsync(dialog);
        }
        catch (Exception unhandledEx)
        {
            CrashReporter.Log($"[SettingsHandler.ApplyUlOsdHotkey_Click] Unhandled error — {unhandledEx.Message}\n{unhandledEx.StackTrace}");
        }
    }

    // ── ReLimiter Shared Presets ──────────────────────────────────────────────

    public void UlSharedPresetsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            ViewModel.Settings.UlSharedPresets = combo.SelectedIndex == 1;
            ViewModel.SaveSettingsPublic();
        }
    }

    public void UlDlssHooksCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            ViewModel.Settings.UlDlssHooks = combo.SelectedIndex == 1;
            ViewModel.SaveSettingsPublic();
        }
    }
}
