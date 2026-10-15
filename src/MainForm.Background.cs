using System;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MiniView.WebView2App
{
    internal sealed partial class MainForm
    {
        private const int HotkeyPreviousVideo = 6;
        private const int HotkeyNextVideo = 7;
        private readonly Panel backgroundPanel = new Panel();
        private readonly Label backgroundPlaybackStatus = new Label();
        private readonly RoundedButton previousVideoButton = CreateSettingsActionButton("上一条", false);
        private readonly RoundedButton nextVideoButton = CreateSettingsActionButton("下一条", false);
        private readonly RoundedButton restoreVideoButton = CreateSettingsActionButton("恢复画面", false);
        private readonly RoundedButton backgroundMuteButton = CreateSettingsActionButton("静音", false);
        private readonly ToolStripMenuItem backgroundMenuItem = new ToolStripMenuItem("后台播放（隐藏画面）");
        private readonly ToggleSwitch backgroundModeToggle = new ToggleSwitch();
        private readonly Label backgroundModeStatus = CreateStatusLabel();
        private readonly RoundedButton previousVideoShortcutButton = CreateShortcutButton();
        private readonly RoundedButton nextVideoShortcutButton = CreateShortcutButton();
        private readonly Label previousVideoShortcutStatus = CreateStatusLabel();
        private readonly Label nextVideoShortcutStatus = CreateStatusLabel();
        private HotkeyDefinition previousVideoHotkey;
        private HotkeyDefinition nextVideoHotkey;
        private bool previousVideoHotkeyAvailable;
        private bool nextVideoHotkeyAvailable;
        private bool backgroundMode;
        private bool videoSwitchPending;
        private long backgroundRevision;
        private Rectangle beforeBackgroundBounds;
        private Size beforeBackgroundMinimumSize;
        private Rectangle backgroundViewport;
        private string lastAutomaticToken;

        private void BuildBackgroundPanel()
        {
            backgroundPanel.Visible = false;
            backgroundPanel.BackColor = UiPalette.Page;
            backgroundPanel.MouseDown += DragWindow;
            backgroundPlaybackStatus.Font = UiFonts.Micro;
            backgroundPlaybackStatus.ForeColor = UiPalette.TextSecondary;
            backgroundPlaybackStatus.AutoEllipsis = true;
            backgroundPlaybackStatus.TextAlign = ContentAlignment.MiddleCenter;
            backgroundPlaybackStatus.MouseDown += DragWindow;
            backgroundPanel.Controls.Add(backgroundPlaybackStatus);
            foreach (RoundedButton button in new[] { previousVideoButton, nextVideoButton, backgroundMuteButton, restoreVideoButton })
                backgroundPanel.Controls.Add(button);
            previousVideoButton.Click += delegate { RequestVideoSwitch(-1); };
            nextVideoButton.Click += delegate { RequestVideoSwitch(1); };
            backgroundMuteButton.Click += delegate { ToggleMute(); };
            restoreVideoButton.Click += delegate { ToggleBackgroundMode(); };
        }

        private void ToggleBackgroundMode()
        {
            if (isQuitting || IsDisposed) return;
            if (!webViewReady) { ShowTransientNotice("网页尚未准备好，请稍后开启后台播放。"); UpdateBackgroundSettings(); return; }
            if (captureTarget != null) CancelShortcutCapture();
            lifecycle.Refresh();
            if (!backgroundMode)
            {
                UndockEdge();
                // Freeze the viewport before shrinking the shell. Hidden-page layout changes can
                // otherwise destroy the feed's virtual list or leave a black compositor on restore.
                beforeBackgroundBounds = Bounds;
                beforeBackgroundMinimumSize = MinimumSize;
                backgroundViewport = webView.Bounds;
                backgroundMode = true;
                webView.Visible = false;
                webView.Dock = DockStyle.None;
                webView.Bounds = backgroundViewport;
                settingsOpen = false;
                settingsPanel.Visible = false;
                suspendTimer.Stop();
                ResumeSuspendedWebView();
                ResizeBackgroundWindow();
            }
            else
            {
                if (!settingsOpen) settings.BackgroundBounds = BoundsData.FromRectangle(Bounds);
                backgroundMode = false;
                backgroundPanel.Visible = false;
                MinimumSize = beforeBackgroundMinimumSize;
                SetBoundsProgrammatically(beforeBackgroundBounds);
                webView.Dock = DockStyle.Fill;
                webView.Visible = Visible && !settingsOpen;
                if (webView.Visible) webView.BringToFront();
                UpdateZoom();
            }
            ApplyChromeState(settings.ChromeHidden);
            webView.SynchronizeVisibility();
            outsideSince = null;
            showGraceUntil = DateTime.UtcNow.AddMilliseconds(ShowGraceMilliseconds);
            lastAutomaticToken = null;
            ApplyMediaState();
            if (!backgroundMode)
            {
                ApplyPageElementVisibility();
                ObserveTask(DetectLiveStateAsync(), "BackgroundRestoreLive");
                // The live state can be unchanged while its layout was deferred in background mode.
                if (currentIsLive) ApplyLiveLayout();
                else if (liveLandscapeApplied) RestorePortraitBounds();
            }
            ScheduleSettingsSave();
            UpdateSettingsUi();
            Diagnostics.Log("background-mode=" + backgroundMode);
        }

        private void ResizeBackgroundWindow()
        {
            if (!backgroundMode) return;
            if (settingsOpen)
            {
                settings.BackgroundBounds = BoundsData.FromRectangle(Bounds);
                MinimumSize = new Size(Scaled(280), Scaled(460));
                SetBoundsProgrammatically(beforeBackgroundBounds);
            }
            else
            {
                MinimumSize = new Size(Scaled(280), Scaled(160));
                SetBoundsProgrammatically(WindowRules.KeepOnScreen(settings.BackgroundBounds,
                    Scaled(280), Scaled(160), new Size(Scaled(360), Scaled(160))));
            }
            LayoutWindow();
        }

        private void LayoutBackgroundPanel()
        {
            backgroundPanel.Bounds = contentHost.ClientRectangle;
            backgroundPanel.Visible = backgroundMode && !settingsOpen;
            int margin = Scaled(10), gap = Scaled(8);
            int width = Math.Max(1, (backgroundPanel.Width - margin * 2 - gap) / 2);
            backgroundPlaybackStatus.SetBounds(margin, Scaled(2), Math.Max(1, backgroundPanel.Width - margin * 2), Scaled(26));
            previousVideoButton.SetBounds(margin, Scaled(32), width, Scaled(30));
            nextVideoButton.SetBounds(margin + width + gap, Scaled(32), width, Scaled(30));
            backgroundMuteButton.SetBounds(margin, Scaled(70), width, Scaled(30));
            restoreVideoButton.SetBounds(margin + width + gap, Scaled(70), width, Scaled(30));
            if (backgroundPanel.Visible) backgroundPanel.BringToFront();
        }

        private void UpdateBackgroundControls()
        {
            backgroundMenuItem.Checked = backgroundMode;
            backgroundMenuItem.Text = backgroundMode ? "恢复视频画面" : "后台播放（隐藏画面）";
            bool canSwitch = backgroundMode && Visible && !currentIsLive && !videoSwitchPending;
            previousVideoButton.Enabled = nextVideoButton.Enabled = canSwitch;
            backgroundPlaybackStatus.Text = currentIsLive ? "直播声音播放中" : videoSwitchPending ? "正在切换视频…" : "后台播放 · 播完自动下一条";
            backgroundMuteButton.Text = settings.Muted ? "恢复声音" : "静音";
            toolTip.SetToolTip(previousVideoButton, "上一条" + FormatShortcut(previousVideoHotkey));
            toolTip.SetToolTip(nextVideoButton, "下一条" + FormatShortcut(nextVideoHotkey));
        }

        private void UpdateBackgroundSettings()
        {
            backgroundModeToggle.Checked = backgroundMode;
            backgroundModeStatus.Text = backgroundMode ? "只播放声音；老板键仍会暂停静音" : "关闭；每次启动需手动开启";
            UpdateVideoShortcutUi("previous", previousVideoHotkeyAvailable);
            UpdateVideoShortcutUi("next", nextVideoHotkeyAvailable);
        }

        private void UpdateVideoShortcutUi(string target, bool available)
        {
            RoundedButton button = ShortcutButtonFor(target);
            HotkeyDefinition hotkey = HotkeyFor(target);
            button.Text = ShortcutButtonText(hotkey);
            button.FaceColor = UiPalette.ShortcutFace;
            ShortcutStatusFor(target).Text = ShortcutStatusText(available || !IsHandleCreated, hotkey);
            toolTip.SetToolTip(button, "仅后台模式生效" + FormatShortcut(hotkey) + "；" + ShortcutHint(hotkey));
        }

        private void ApplyBackgroundState()
        {
            ObserveTask(ApplyBackgroundStateAsync(), "ApplyBackgroundState");
        }

        private async Task ApplyBackgroundStateAsync()
        {
            long revision = ++backgroundRevision;
            if (!webViewReady || webView.CoreWebView2 == null || webViewSuspended || isQuitting) return;
            await webView.ExecuteScriptAsync(BackgroundScripts.Build(revision, navigationGeneration,
                backgroundMode, backgroundMode && Visible, currentIsLive));
        }

        private void RequestVideoSwitch(int direction)
        {
            ObserveTask(SwitchVideoAsync(direction, null), "SwitchVideo");
        }

        private async Task<string> ReadBackgroundTokenAsync()
        {
            return new JavaScriptSerializer().Deserialize<string>(await webView.ExecuteScriptAsync(BackgroundScripts.Token));
        }

        private bool CanSwitchVideo(long revision, int navigation, long operation)
        {
            return backgroundMode && Visible && !currentIsLive && !isQuitting && !IsDisposed
                && webViewReady && !webViewSuspended && revision == backgroundRevision
                && navigation == navigationGeneration && lifecycle.IsCurrent(operation);
        }

        private async Task SwitchVideoAsync(int direction, string automaticToken)
        {
            if (!backgroundMode || !Visible || currentIsLive || !webViewReady || webViewSuspended || videoSwitchPending || isQuitting) return;
            long revision = backgroundRevision, operation = lifecycle.Generation;
            int navigation = navigationGeneration;
            videoSwitchPending = true;
            UpdateBackgroundControls();
            try
            {
                string token = await ReadBackgroundTokenAsync();
                if (!CanSwitchVideo(revision, navigation, operation)) return;
                if (automaticToken != null && automaticToken != token) return;
                if (string.IsNullOrEmpty(token)) { ShowTransientNotice("当前页面没有可切换的视频，请恢复画面检查。"); return; }
                string action = new JavaScriptSerializer().Deserialize<string>(await webView.ExecuteScriptAsync(BackgroundScripts.Switch(direction)));
                if (!CanSwitchVideo(revision, navigation, operation)) return;
                if (action == "keyboard")
                {
                    string key = direction < 0 ? "ArrowUp" : "ArrowDown";
                    int code = direction < 0 ? 38 : 40;
                    string parameters = "\"key\":\"" + key + "\",\"code\":\"" + key + "\",\"windowsVirtualKeyCode\":" + code + ",\"nativeVirtualKeyCode\":" + code;
                    // Send only to this WebView. Always release even if a hide invalidates the operation.
                    Exception keyError = null;
                    try { await webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\"," + parameters + "}"); }
                    catch (Exception exception) { keyError = exception; }
                    await webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\"," + parameters + "}");
                    if (keyError != null) throw new InvalidOperationException("Video key dispatch failed", keyError);
                }
                else if (action != "native")
                {
                    ShowTransientNotice("当前页面暂不支持切换，请恢复画面检查登录或推荐列表。");
                    return;
                }
                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline && CanSwitchVideo(revision, navigation, operation))
                {
                    string next = await ReadBackgroundTokenAsync();
                    if (!CanSwitchVideo(revision, navigation, operation)) return;
                    if (!string.IsNullOrEmpty(next) && next != token) return;
                    await Task.Delay(80);
                }
                if (CanSwitchVideo(revision, navigation, operation)) ShowTransientNotice("视频切换未完成，请恢复画面检查后重试。");
            }
            catch (Exception exception)
            {
                Diagnostics.LogException("SwitchVideo", exception);
                if (CanSwitchVideo(revision, navigation, operation)) ShowTransientNotice("视频切换失败，请恢复画面后重试。");
            }
            finally
            {
                videoSwitchPending = false;
                if (!isQuitting && !IsDisposed) UpdateBackgroundControls();
            }
        }

        private bool HandleBackgroundMessage(string message)
        {
            if (!message.StartsWith("boniu:background-ended:", StringComparison.Ordinal)) return false;
            string[] parts = message.Split(':');
            int navigation; long revision; long token;
            if (parts.Length != 5 || !int.TryParse(parts[2], out navigation)
                || !long.TryParse(parts[3], out revision) || !long.TryParse(parts[4], out token)) return true;
            if (!backgroundMode || !Visible || currentIsLive || navigation != navigationGeneration || revision != backgroundRevision) return true;
            string key = navigation.ToString(CultureInfo.InvariantCulture) + ":" + parts[4];
            if (lastAutomaticToken == key) return true;
            lastAutomaticToken = key;
            ObserveTask(SwitchVideoAsync(1, parts[4]), "AutomaticNextVideo");
            return true;
        }
    }
}
