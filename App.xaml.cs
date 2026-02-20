using System;
using System.Windows;
using System.Windows.Media;
using MergeMansionWikiTools.Services;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Apply theme early (before MainWindow renders) based on saved preference
            var settings = SettingsService.Load();
            ApplyTheme(settings.ThemePreference);
        }

        public static void ApplyTheme(string preference)
        {
            ApplicationTheme theme;

            if (preference == "Light")
                theme = ApplicationTheme.Light;
            else if (preference == "Dark")
                theme = ApplicationTheme.Dark;
            else // "System"
            {
                var sys = ApplicationThemeManager.GetSystemTheme();
                theme = sys is SystemTheme.Dark or SystemTheme.HCBlack or SystemTheme.HC2
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light;
            }

            // Apply theme WITH system accent (true) so both themes use the user's color scheme
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);

            // Fix: WPF UI computes its own dark-theme accent variants which don't match Windows.
            // Read the actual accent palette from the registry for exact color match.
            if (theme == ApplicationTheme.Dark)
            {
                var accent = ApplicationAccentColorManager.GetColorizationColor();
                ApplyDarkAccentFix(accent);
            }

            // Update custom theme-aware brushes
            var isLight = theme == ApplicationTheme.Light;
            Current.Resources["ChainExpandedContentBackground"] = new SolidColorBrush(
                isLight ? Color.FromArgb(0x20, 0, 0, 0) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            Current.Resources["SidebarItemSelectedBackground"] = new SolidColorBrush(
                isLight ? Color.FromArgb(0x0A, 0, 0, 0) : Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
        }

        /// <summary>
        /// Updates only accent color resources without re-applying theme or backdrop.
        /// Called when the user changes accent color in Windows Settings while the app is running.
        /// </summary>
        internal static void RefreshAccentOnly()
        {
            var accent = ApplicationAccentColorManager.GetColorizationColor();
            var currentTheme = ApplicationThemeManager.GetAppTheme();

            if (currentTheme == ApplicationTheme.Dark)
            {
                ApplyDarkAccentFix(accent);
            }
            else
            {
                // For light theme, Apply sets Light variants + computes Dark variants from accent.
                // Dark variants are what light-theme buttons actually use, so this works correctly.
                var palette = GetSystemAccentPalette();
                if (palette is var (light1, light2, light3))
                    ApplicationAccentColorManager.Apply(accent, light1, light2, light3);
                else
                    ApplicationAccentColorManager.Apply(accent, accent,
                        LightenColor(accent, 0.35), LightenColor(accent, 0.45));
            }
        }

        /// <summary>
        /// Applies the correct dark-theme accent variants by reading the Windows accent palette
        /// from the registry. Falls back to custom lightening if the palette is unavailable.
        /// </summary>
        internal static void ApplyDarkAccentFix(Color accent)
        {
            var palette = GetSystemAccentPalette();
            if (palette is var (light1, light2, light3))
            {
                ApplicationAccentColorManager.Apply(accent, light1, light2, light3);
            }
            else
            {
                // Fallback: custom lightening that preserves saturation
                ApplicationAccentColorManager.Apply(
                    accent, accent,
                    LightenColor(accent, 0.35),
                    LightenColor(accent, 0.45));
            }
        }

        /// <summary>
        /// Reads the Windows accent color palette from the registry.
        /// Returns (Light1, Light2, Light3) variants that Windows uses for dark-theme UI elements.
        /// </summary>
        private static (Color light1, Color light2, Color light3)? GetSystemAccentPalette()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                if (key?.GetValue("AccentPalette") is not byte[] palette || palette.Length < 12)
                    return null;

                // AccentPalette: 8 colors × 4 bytes (R,G,B,pad), lightest → darkest
                // [0..3]=Light3, [4..7]=Light2, [8..11]=Light1, [12..15]=Base, ...
                return (
                    Color.FromRgb(palette[8],  palette[9],  palette[10]),  // Light1 (primary)
                    Color.FromRgb(palette[4],  palette[5],  palette[6]),   // Light2 (secondary)
                    Color.FromRgb(palette[0],  palette[1],  palette[2])    // Light3 (tertiary)
                );
            }
            catch
            {
                return null;
            }
        }

        internal static Color LightenColor(Color color, double amount)
        {
            var r = (byte)Math.Min(255, color.R + (255 - color.R) * amount);
            var g = (byte)Math.Min(255, color.G + (255 - color.G) * amount);
            var b = (byte)Math.Min(255, color.B + (255 - color.B) * amount);
            return Color.FromArgb(color.A, r, g, b);
        }
    }
}
