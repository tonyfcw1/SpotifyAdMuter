using System.Diagnostics;
using SpotifyAdMuter.Properties;
using System.Runtime.InteropServices;

namespace SpotifyAdMuter
{
    public class Context : ApplicationContext
    {
        private Process? _spotifyProcess;
        private AppMuter? _spotifyMuter;
        private string _spotifyWindowName = string.Empty;
        private bool _isMuted = false;
        private readonly NotifyIcon _notifyIcon;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly DiagnosticsForm _diagnostics = new();
        private readonly WinEventDelegate _winEventCallback;
        private IntPtr _winEventHook;
        private DateTime _nextAudioSessionLookup = DateTime.MinValue;

        private const uint EventObjectNameChange = 0x800C;
        private const uint WineventOutOfContext = 0x0000;
        private const int ObjIdWindow = 0;

        private Process? SpotifyProcess
        {
            get => _spotifyProcess;
            set
            {
                _spotifyProcess = value;
                Debug.WriteLine($"SpotifyProcess.Id = {value?.Id}");
            }
        }

        private string SpotifyWindowName
        {
            get => _spotifyWindowName;
            set
            {
                _spotifyWindowName = value;
                Debug.WriteLine($"SpotifyWindowName = {value}");

            }
        }

        public Context()
        {
            _winEventCallback = OnWinEvent;
            _notifyIcon = new NotifyIcon()
            {
                Text = "unmuted",
                Icon = Resources.UnmutedIcon,
                ContextMenuStrip = new ContextMenuStrip()
                {
                    Items =
                    {
                        new ToolStripMenuItem("Diagnostics", null, ShowDiagnostics),
                        new ToolStripMenuItem("Exit", null, Exit)
                    }
                },
                Visible = true
            };
            _notifyIcon.MouseClick += NotifyIcon_MouseClick;
            _winEventHook = SetWinEventHook(EventObjectNameChange, EventObjectNameChange,
                IntPtr.Zero, _winEventCallback, 0, 0, WineventOutOfContext);
            Log(_winEventHook == IntPtr.Zero
                ? "Window-title event hook unavailable; using 2s polling fallback."
                : "Monitor started; listening for window-title changes, 2s polling fallback.");
            _timer = new System.Windows.Forms.Timer()
            {
                Interval = 2000,
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (SpotifyProcess == null)
            {
                MakeNewSpotifyProcess();
                if (SpotifyProcess == null) return;
            }
            if (!SpotifyIsRunning())
            {
                Log("Spotify process exited.");
                SpotifyProcess = null;
                _spotifyMuter = null;
                return;
            }

            if (_spotifyMuter == null && DateTime.UtcNow >= _nextAudioSessionLookup)
            {
                _nextAudioSessionLookup = DateTime.UtcNow.AddSeconds(1);
                MakeNewSpotifyMuter();
            }

            UpdateMuteState("poll");
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId,
            int childId, uint eventThread, uint eventTime)
        {
            try
            {
                if (hwnd == IntPtr.Zero || objectId != ObjIdWindow || childId != 0 || SpotifyProcess == null)
                    return;

                GetWindowThreadProcessId(hwnd, out uint processId);
                if (processId == SpotifyProcess.Id)
                    UpdateMuteState("title event");
            }
            catch (Exception ex)
            {
                Log($"Window-title event error: {ex.Message}");
            }
        }

        private void ShowDiagnostics(object? sender, EventArgs e)
        {
            _diagnostics.Show();
            _diagnostics.BringToFront();
        }

        private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleMute();
                Log($"Manual mute toggle: {(_isMuted ? "muted" : "unmuted")}.");
            }
        }

        private void MakeNewSpotifyProcess()
        {
            SpotifyWindowName = string.Empty;
            _nextAudioSessionLookup = DateTime.MinValue;
            foreach (Process process in Process.GetProcessesByName("spotify"))
            {
                if (process.MainWindowTitle.Length != 0)
                {
                    SpotifyProcess = process;
                    Log($"Spotify found (PID {process.Id}).");
                    return;
                }
            }
        }
        private void MakeNewSpotifyMuter()
        {
            Debug.Assert(SpotifyProcess != null);
            _spotifyMuter = new AppMuter(SpotifyProcess.Id);
            if (!_spotifyMuter.IsActive())
            {
                _spotifyMuter = null;
                Log("Spotify audio session not available yet; retrying shortly.");
            }
            else
            {
                _spotifyMuter.GetMute(out _isMuted);
                SpotifyWindowName = string.Empty;
                Log($"Audio session attached; initial mute state: {(_isMuted ? "muted" : "unmuted")}.");
            }
        }

        private void UpdateMuteState(string source)
        {
            if (SpotifyProcess == null) return;

            SpotifyProcess.Refresh();
            string title = SpotifyProcess.MainWindowTitle;
            if (SpotifyWindowName.Equals(title)) return;

            SpotifyWindowName = title;
            bool isAd = SpotifyIsPlayingAd();
            Log($"{source}: title=\"{title}\"; classified={(isAd ? "ad" : "content")}; muted={_isMuted}.");
            if (_spotifyMuter != null && isAd != _isMuted)
            {
                ToggleMute();
                Log($"Mute changed to {(_isMuted ? "muted" : "unmuted")}.");
            }
        }
        private bool SpotifyIsRunning()
        {
            if (SpotifyProcess == null)
                return false;
            SpotifyProcess.Refresh();
            return !SpotifyProcess.HasExited;
        }
        private bool SpotifyIsPlayingAd()
        {
            return !SpotifyWindowName.Contains(" - ");
        }
        private const int NotifyIconTextMaxLength = 127;
        private void ToggleMute()
        {
            _spotifyMuter?.SetMute(_isMuted ^= true);
            if (_isMuted)
            {
                _notifyIcon.Text = ToSafeNotifyIconText($"muted \"{SpotifyWindowName}\"");
                _notifyIcon.Icon = Resources.MutedIcon;
            }
            else
            {
                _notifyIcon.Text = ToSafeNotifyIconText($"unmuted \"{SpotifyWindowName}\"");
                _notifyIcon.Icon = Resources.UnmutedIcon;
            }

        }
        private static string ToSafeNotifyIconText(string text)
        {
            if (text == null) return string.Empty;
            if (text.Length <= NotifyIconTextMaxLength) return text;
            return text.Substring(0, NotifyIconTextMaxLength);
        }
        void Exit(object? sender, EventArgs e)
        {
            _spotifyMuter?.SetMute(false);
            _timer.Stop();
            if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
            _notifyIcon.Visible = false;
            Application.Exit();
        }

        private void Log(string message)
        {
            _diagnostics.AddEntry($"{DateTime.Now:HH:mm:ss.fff}  {message}");
        }

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);
    }
}
