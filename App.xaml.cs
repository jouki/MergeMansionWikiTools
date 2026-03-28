using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MergeMansionWikiTools.Services;
using MergeMansionWikiTools.Views;
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

            // Fix Copy/Cut on TextBoxes: WPF UI TextBox's default handler doesn't
            // flush clipboard (no OLE render), so WIN+V doesn't capture it and Cut
            // doesn't reliably remove text. We intercept via PreviewExecuted and use
            // native Win32 clipboard API (fast, no freeze, immediately rendered for WIN+V).
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.TextBox),
                CommandManager.PreviewExecutedEvent,
                new ExecutedRoutedEventHandler(OnTextBoxCopyCut));

            // Shift+MouseWheel → horizontal scroll on any ScrollViewer
            EventManager.RegisterClassHandler(typeof(ScrollViewer),
                UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(OnScrollViewerShiftWheel));

            AppLogger.Init();

            // Apply theme early (before MainWindow renders) based on saved preference
            var settings = SettingsService.Load();
            ApplyTheme(settings.ThemePreference);

            // Splash: fade in 1s (smooth) → load app behind splash → fade out 0.2s
            var splash = new SplashWindow();
            // Center splash on the monitor where the main window was last used
            if (settings.WindowLeft.HasValue && settings.WindowTop.HasValue)
            {
                double winW = settings.WindowWidth ?? 1380;
                double winH = settings.WindowHeight ?? 963;
                // Place splash temporarily at saved window center so WPF assigns it to the correct monitor
                splash.WindowStartupLocation = WindowStartupLocation.Manual;
                splash.Left = settings.WindowLeft.Value + (winW - 400) / 2;
                splash.Top = settings.WindowTop.Value + (winH - 400) / 2;
                // After the window handle is created, use it to find the actual monitor working area
                splash.SourceInitialized += (_, _) =>
                {
                    var source = System.Windows.Interop.HwndSource.FromHwnd(
                        new System.Windows.Interop.WindowInteropHelper(splash).Handle);
                    if (source?.CompositionTarget != null)
                    {
                        var dpi = source.CompositionTarget.TransformToDevice;
                        // Convert DIP center to physical pixels for MonitorFromPoint
                        double cx = (splash.Left + 200) * dpi.M11;
                        double cy = (splash.Top + 200) * dpi.M22;
                        var hMon = MonitorFromPoint(new POINT((int)cx, (int)cy), 2);
                        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                        if (GetMonitorInfo(hMon, ref mi))
                        {
                            // Convert monitor working area back to DIP
                            double monL = mi.rcWork.left / dpi.M11;
                            double monT = mi.rcWork.top / dpi.M22;
                            double monW = (mi.rcWork.right - mi.rcWork.left) / dpi.M11;
                            double monH = (mi.rcWork.bottom - mi.rcWork.top) / dpi.M22;
                            splash.Left = monL + (monW - 400) / 2;
                            splash.Top = monT + (monH - 400) / 2;
                        }
                    }
                };
            }
            MainWindow = splash;
            splash.Show();

            splash.RunFadeSequence(
                startFadeOut =>
                {
                    using var _ = AppLogger.Timed("App.Startup");

                    // OOBE: show setup wizard for first-time users
                    if (!settings.OobeCompleted)
                    {
                        splash.Hide();
                        var wizard = new SetupWizard();
                        wizard.ShowDialog();

                        if (!wizard.CompletedNormally)
                        {
                            // X button = user cancelled → shut down app entirely
                            splash.Close();
                            Shutdown();
                            return;
                        }

                        settings = SettingsService.Load(); // Wizard saved its own copy
                    }

                    // Fade-in done — splash is static, safe to load MainWindow
                    var main = new MainWindow();
                    main.Show();
                    MainWindow = main;

                    // Start fade-out once MainWindow is fully loaded
                    if (main.IsLoaded)
                        startFadeOut();
                    else
                        main.Loaded += (_, _) => startFadeOut();
                },
                () => splash.Close()
            );
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLogger.Flush();
            base.OnExit(e);
        }

        // ── Monitor detection (for splash centering on correct monitor) ──

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int x, y; public POINT(int x, int y) { this.x = x; this.y = y; } }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        // ── Native Win32 clipboard (fast, no OLE freeze, rendered for WIN+V) ──

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EmptyClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        internal static bool NativeSetClipboardText(string text)
        {
            if (!OpenClipboard(IntPtr.Zero))
                return false;
            try
            {
                EmptyClipboard();
                var chars = text + "\0";
                var bytes = System.Text.Encoding.Unicode.GetBytes(chars);
                var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (hGlobal == IntPtr.Zero) return false;
                var ptr = GlobalLock(hGlobal);
                if (ptr == IntPtr.Zero) return false;
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, ptr, bytes.Length);
                GlobalUnlock(hGlobal);
                SetClipboardData(CF_UNICODETEXT, hGlobal);
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static void OnTextBoxCopyCut(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox tb || tb.SelectionLength <= 0)
                return;

            if (e.Command == ApplicationCommands.Copy)
            {
                NativeSetClipboardText(tb.SelectedText);
                e.Handled = true;
            }
            else if (e.Command == ApplicationCommands.Cut)
            {
                NativeSetClipboardText(tb.SelectedText);
                if (!tb.IsReadOnly)
                {
                    var start = tb.SelectionStart;
                    var len = tb.SelectionLength;
                    tb.Text = tb.Text.Remove(start, len);
                    tb.SelectionStart = start;
                }
                e.Handled = true;
            }
        }

        private static void OnScrollViewerShiftWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
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
