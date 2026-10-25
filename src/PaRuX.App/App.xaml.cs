using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PaRuX.Services;
using PaRuX.Shell;

namespace PaRuX
{
    public partial class App : Application
    {
        public static MainWindow? MainWin { get; private set; }
        public static BubbleWindow? BubbleWin { get; private set; }
        public static HotkeyService Hotkey { get; } = new HotkeyService();

        /// <summary>Set only by the tray "Exit" path, so window close can mean "hide".</summary>
        public static bool IsShuttingDown { get; private set; }

        private const string SingleInstanceMutexName = "Local\\PaRuX.App.SingleInstance";
        private static Mutex? _singleInstanceMutex;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            bool createdNew;
            string mutexName = SingleInstanceMutexName;
#if DEBUG
            string? uiTestDataRoot = Environment.GetEnvironmentVariable("PARUX_DATA_ROOT");
            // PARUX_UI_TEST_ISOLATED is set for every FlaUI-launched instance (PARUX_UI_TESTS only marks
            // fixture runs), so a test never collides with a real or concurrently running PaRuX.
            if ((string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal)
                    || string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_ISOLATED"), "1", StringComparison.Ordinal))
                && !string.IsNullOrWhiteSpace(uiTestDataRoot))
            {
                string testScope = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(uiTestDataRoot))))[..16];
                mutexName += ".UITest." + testScope;
            }
#endif
            _singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
            if (!createdNew)
            {
                Log.Write("PaRuX already running - activating existing instance and exiting.");
                ActivateExistingInstance();
                Environment.Exit(0);
                return;
            }

            // Startup is different from an ordinary post-launch rendering exception. If any
            // prerequisite fails before MainWindow exists, swallowing the WinUI unhandled event
            // can leave an invisible process holding the single-instance mutex. Fail closed here:
            // preserve the full diagnostic and terminate instead of creating a zombie instance.
            try
            {
                AppPaths.EnsureCreated();
                SettingsStore.Current.Load();
                LocalAiController.Current.StartWatching();
                GamingManager.Current.Start();
                AgentCodingWorkerService.Current.Start();
                AgentBinaryAnalysisWorkerService.Current.Start();
                UiVerificationWorkerService.Current.Start();
                ComputerUseWorkerService.Current.Start();
                Log.Write("PaRuX starting.");

                MainWin = new MainWindow();
                MainWin.Activate();

                BubbleWin = new BubbleWindow();
                BubbleWin.ApplyVisibilityMode();

                ApplyTheme();

                // Start loading the selected text model as soon as the shell is usable. This is
                // deliberately fire-and-forget: OFF and Gaming modes are respected by Core, while
                // startup remains responsive and the first chat no longer pays avoidable idle time.
                _ = WarmLocalAiAsync();
            }
            catch (Exception ex)
            {
                Log.Write("FATAL STARTUP: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex);
                try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
                try { _singleInstanceMutex?.Dispose(); } catch { }
                Environment.Exit(1);
            }
        }

        private static async Task WarmLocalAiAsync()
        {
            try
            {
                LocalAiController.ActionResult result = await LocalAiController.Current
                    .RunActionAsync("Ensure", CancellationToken.None).ConfigureAwait(false);
                Log.Write("Startup model warmup -> success=" + result.Success + " : " + result.Message);
            }
            catch (Exception ex)
            {
                Log.Write("Startup model warmup failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ActivateExistingInstance()
        {
            try
            {
                IntPtr hwnd = FindWindow(null, "RBG PaRuX AI Studio");
                if (hwnd == IntPtr.Zero) return;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            catch (Exception ex)
            {
                Log.Write("ActivateExistingInstance failed: " + ex.Message);
            }
        }

        public static ElementTheme CurrentElementTheme
        {
            get
            {
                string theme = SettingsStore.Current.Data.Theme;
                if (theme == "Light") return ElementTheme.Light;
                if (theme == "Dark") return ElementTheme.Dark;
                return ElementTheme.Default;
            }
        }

        public static void ApplyTheme()
        {
            ElementTheme theme = CurrentElementTheme;

            if (MainWin != null)
            {
                FrameworkElement? root = MainWin?.Content as FrameworkElement;
                if (root != null) root.RequestedTheme = theme;
            }
            if (BubbleWin != null)
            {
                FrameworkElement? root = BubbleWin?.Content as FrameworkElement;
                if (root != null) root.RequestedTheme = theme;
            }
        }

        public static void ShowMainWindow()
        {
            if (MainWin == null) return;
            try
            {
                MainWin.AppWindow.Show();
                MainWin.Activate();
                MainWin.BringToFront();
            }
            catch (Exception ex)
            {
                Log.Write("ShowMainWindow failed: " + ex.Message);
            }
        }

        public static void ExitApplication()
        {
            IsShuttingDown = true;
            try
            {
                ChatSession.Current.Cancel();
                ChatSession.Current.Persist();
                Hotkey.Dispose();
                if (BubbleWin != null) BubbleWin.PersistPosition();
                if (MainWin != null) MainWin.DisposeTray();
            }
            catch (Exception ex)
            {
                Log.Write("Shutdown cleanup failed: " + ex.Message);
            }
            Log.Write("PaRuX exiting.");
            Current.Exit();
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log.Write("UNHANDLED: " + e.Message + "\n" + (e.Exception == null ? "" : e.Exception.ToString()));
            // Post-launch UI exceptions are logged and kept recoverable where WinUI allows it.
            // Fatal bootstrap work is handled separately in OnLaunched and never swallowed here.
            e.Handled = true;
        }
    }
}
