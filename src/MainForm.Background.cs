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
        private readonly TrackBar backgroundVolumeSlider = new TrackBar();
        private readonly Label backgroundVolumeLabel = new Label();
        private readonly RoundedButton previousVideoButton = CreateSettingsActionButton("上一条", false);
        private readonly RoundedButton nextVideoButton = CreateSettingsActionButton("下一条", false);
        private readonly RoundedButton restoreVideoButton = CreateSettingsActionButton("恢复画面", false);
        private readonly RoundedButton backgroundMuteButton = CreateSettingsActionButton("静音", false);
        private readonly RoundedButton backgroundPlayButton = CreateSettingsActionButton("暂停播放", false);
        private readonly RoundedButton backgroundRepeatButton = CreateSettingsActionButton("循环当前：关", false);
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
        private bool backgroundRepeatCurrent;
        private bool backgroundHasVideo;
        private bool backgroundPaused;
        private bool backgroundPlaybackPending;

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
            backgroundVolumeLabel.Font = UiFonts.Micro;
            backgroundVolumeLabel.ForeColor = UiPalette.TextSecondary;
            backgroundVolumeLabel.TextAlign = ContentAlignment.MiddleLeft;
            backgroundVolumeSlider.Minimum = 0;
            backgroundVolumeSlider.Maximum = 100;
            backgroundVolumeSlider.SmallChange = 5;
            backgroundVolumeSlider.LargeChange = 10;
            backgroundVolumeSlider.TickStyle = TickStyle.None;
            backgroundVolumeSlider.AutoSize = false;
            backgroundVolumeSlider.BackColor = UiPalette.Page;
            backgroundVolumeSlider.AccessibleName = "后台音量";
            backgroundVolumeSlider.Value = settings.BackgroundVolume;
            backgroundVolumeSlider.ValueChanged += delegate
            {
                settings.BackgroundVolume = backgroundVolumeSlider.Value;
                UpdateBackgroundControls();
                ScheduleSettingsSave();
                ObserveTask(ApplyBackgroundVolumeAsync(), "BackgroundVolume");
            };
            backgroundPanel.Controls.Add(backgroundVolumeLabel);
            backgroundPanel.Controls.Add(backgroundVolumeSlider);
            foreach (RoundedButton button in new[] { previousVideoButton, nextVideoButton, backgroundMuteButton, restoreVideoButton, backgroundPlayButton, backgroundRepeatButton })
                backgroundPanel.Controls.Add(button);
            previousVideoButton.Click += delegate { RequestVideoSwitch(-1); };
            nextVideoButton.Click += delegate { RequestVideoSwitch(1); };
            backgroundMuteButton.Click += delegate { ToggleMute(); };
            restoreVideoButton.Click += delegate { ToggleBackgroundMode(); };
            backgroundPlayButton.Click += delegate { ObserveTask(ToggleBackgroundPlaybackAsync(), "BackgroundPlayback"); };
            backgroundRepeatButton.Click += delegate
            {
                if (!backgroundMode || currentIsLive) return;
                backgroundRepeatCurrent = !backgroundRepeatCurrent;
                lastAutomaticToken = null;
                ApplyBackgroundState();
                UpdateBackgroundControls();
            };
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
                backgroundRepeatCurrent = false;
                webView.Visible = false;
                webView.Dock = DockStyle.None;
                ParkBackgroundWebView();
                settingsOpen = false;
                settingsPanel.Visible = false;
                suspendTimer.Stop();
                ResumeSuspendedWebView();
                ResizeBackgroundWindow();
            }
            else
            {
                if (!settingsOpen) settings.BackgroundBounds = BoundsData.FromRectangle(Bounds);
                webView.Visible = false;
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
                MinimumSize = new Size(Scaled(280), Scaled(240));
                SetBoundsProgrammatically(WindowRules.KeepOnScreen(settings.BackgroundBounds,
                    Scaled(280), Scaled(240), new Size(Scaled(360), Scaled(240))));
            }
            LayoutWindow();
        }

        private void LayoutBackgroundPanel()
        {
            if (backgroundMode) ParkBackgroundWebView();
            backgroundPanel.Bounds = contentHost.ClientRectangle;
            backgroundPanel.Visible = backgroundMode && !settingsOpen;
            int margin = Scaled(10), gap = Scaled(8);
            int width = Math.Max(1, (backgroundPanel.Width - margin * 2 - gap) / 2);
            backgroundPlaybackStatus.SetBounds(margin, Scaled(2), Math.Max(1, backgroundPanel.Width - margin * 2), Scaled(26));
            previousVideoButton.SetBounds(margin, Scaled(32), width, Scaled(30));
            nextVideoButton.SetBounds(margin + width + gap, Scaled(32), width, Scaled(30));
            backgroundMuteButton.SetBounds(margin, Scaled(70), width, Scaled(30));
            restoreVideoButton.SetBounds(margin + width + gap, Scaled(70), width, Scaled(30));
            backgroundPlayButton.SetBounds(margin, Scaled(108), width, Scaled(30));
            backgroundRepeatButton.SetBounds(margin + width + gap, Scaled(108), width, Scaled(30));
            backgroundVolumeLabel.SetBounds(margin, Scaled(146), Scaled(82), Scaled(30));
            backgroundVolumeSlider.SetBounds(margin + Scaled(82), Scaled(146),
                Math.Max(1, backgroundPanel.Width - margin * 2 - Scaled(82)), Scaled(30));
            if (backgroundPanel.Visible) backgroundPanel.BringToFront();
        }

        private void ParkBackgroundWebView()
        {
            // Likes/favorites finish slide transitions on animation frames. IsVisible=false freezes
            // those callbacks and leaves the carousel locked after its first background switch.
            // Keep its original viewport active outside the host's client area; no picture is exposed.
            int width = Math.Max(1, backgroundViewport.Width), height = Math.Max(1, backgroundViewport.Height);
            webView.Dock = DockStyle.None;
            webView.Bounds = new Rectangle(-width - Scaled(8), 0, width, height);
            webView.Visible = Visible;
        }

        private void UpdateBackgroundControls()
        {
            backgroundMenuItem.Checked = backgroundMode;
            backgroundMenuItem.Text = backgroundMode ? "恢复视频画面" : "后台播放（隐藏画面）";
            bool standaloneLive = currentIsLive && webView.Source != null && WindowRules.IsLiveUrl(webView.Source.AbsoluteUri);
            bool canSwitch = backgroundMode && Visible && !standaloneLive && !videoSwitchPending;
            previousVideoButton.Enabled = nextVideoButton.Enabled = canSwitch;
            backgroundPlaybackStatus.Text = !string.IsNullOrEmpty(transientNotice) ? transientNotice
                : videoSwitchPending ? "正在切换视频…"
                : standaloneLive ? "独立直播间 · 恢复画面可返回推荐流"
                : backgroundHasVideo && backgroundPaused ? "已暂停播放"
                : currentIsLive ? "直播声音播放中 · 可手动切换"
                : backgroundRepeatCurrent ? "后台播放 · 循环当前视频" : "后台播放 · 播完自动下一条";
            toolTip.SetToolTip(backgroundPlaybackStatus, backgroundPlaybackStatus.Text);
            backgroundMuteButton.Text = settings.Muted ? "恢复声音" : "静音";
            backgroundPlayButton.Text = backgroundPaused ? "继续播放" : "暂停播放";
            backgroundPlayButton.Enabled = backgroundMode && Visible && backgroundHasVideo && !backgroundPlaybackPending && !videoSwitchPending;
            backgroundRepeatButton.Text = backgroundRepeatCurrent ? "循环当前：开" : "循环当前：关";
            backgroundRepeatButton.Enabled = backgroundMode && Visible && backgroundHasVideo && !currentIsLive && !videoSwitchPending;
            backgroundVolumeLabel.Text = "音量 " + settings.BackgroundVolume + "%";
            toolTip.SetToolTip(backgroundVolumeSlider, settings.Muted ? "当前已静音，点击恢复声音后生效" : "后台音量 " + settings.BackgroundVolume + "%");
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
                backgroundMode, backgroundMode && Visible, currentIsLive, settings.BackgroundVolume, backgroundRepeatCurrent));
            if (revision == backgroundRevision) await RefreshBackgroundPlaybackAsync();
        }

        private void ReadBackgroundPlayback(BackgroundPlaybackState state)
        {
            if (state == null) return;
            backgroundHasVideo = state.HasVideo;
            backgroundPaused = state.Paused;
            UpdateBackgroundControls();
        }

        private async Task RefreshBackgroundPlaybackAsync()
        {
            if (!backgroundMode || !Visible || !webViewReady || webViewSuspended || isQuitting) return;
            long revision = backgroundRevision;
            int navigation = navigationGeneration;
            var state = new JavaScriptSerializer().Deserialize<BackgroundPlaybackState>(await webView.ExecuteScriptAsync(BackgroundScripts.PlaybackState));
            if (backgroundMode && revision == backgroundRevision && navigation == navigationGeneration && Visible) ReadBackgroundPlayback(state);
        }

        private async Task ToggleBackgroundPlaybackAsync()
        {
            if (!backgroundMode || !Visible || !webViewReady || webViewSuspended || backgroundPlaybackPending || videoSwitchPending) return;
            long revision = backgroundRevision, operation = lifecycle.Generation;
            int navigation = navigationGeneration;
            backgroundPlaybackPending = true;
            UpdateBackgroundControls();
            try
            {
                string result = new JavaScriptSerializer().Deserialize<string>(await webView.ExecuteScriptAsync(BackgroundScripts.TogglePlayback));
                if (!CanSwitchVideo(revision, navigation, operation)) return;
                if (result == "blocked") ShowTransientNotice("当前视频不可操作，请恢复画面检查。");
                await RefreshBackgroundPlaybackAsync();
            }
            finally { backgroundPlaybackPending = false; if (!IsDisposed) UpdateBackgroundControls(); }
        }

        private sealed class BackgroundPlaybackState
        {
            public bool Live { get; set; }
            public bool HasVideo { get; set; }
            public bool Paused { get; set; }
        }

        private async Task ApplyBackgroundVolumeAsync()
        {
            if (!backgroundMode || !webViewReady || webView.CoreWebView2 == null || webViewSuspended || isQuitting) return;
            await webView.ExecuteScriptAsync(BackgroundScripts.Volume(backgroundRevision, settings.BackgroundVolume));
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
            return backgroundMode && Visible && !isQuitting && !IsDisposed
                && webViewReady && !webViewSuspended && revision == backgroundRevision
                && navigation == navigationGeneration && lifecycle.IsCurrent(operation);
        }

        private async Task SwitchVideoAsync(int direction, string automaticToken)
        {
            if (!backgroundMode || !Visible || (automaticToken != null && currentIsLive) || !webViewReady || webViewSuspended || videoSwitchPending || isQuitting) return;
            long revision = backgroundRevision, operation = lifecycle.Generation;
            int navigation = navigationGeneration;
            videoSwitchPending = true;
            UpdateBackgroundControls();
            try
            {
                ClearTransientNotice();
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
                    ShowTransientNotice(action == "live-room" ? "独立直播间，请恢复画面返回推荐流。"
                        : action == "boundary" ? (direction < 0 ? "已经是列表第一条。" : "已经是列表最后一条。")
                        : action == "blocked" ? "请恢复画面处理弹窗后再切换。" : "此页不支持切换，请返回推荐流。");
                    return;
                }
                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                DateTime wheelFallbackAt = DateTime.UtcNow.AddMilliseconds(1500);
                bool wheelAttempted = false;
                while (DateTime.UtcNow < deadline && CanSwitchVideo(revision, navigation, operation))
                {
                    string next = await ReadBackgroundTokenAsync();
                    if (!CanSwitchVideo(revision, navigation, operation)) return;
                    if (!string.IsNullOrEmpty(next) && next != token) return;
                    if (!wheelAttempted && DateTime.UtcNow >= wheelFallbackAt)
                    {
                        wheelAttempted = true;
                        await webView.ExecuteScriptAsync(BackgroundScripts.Wheel(direction, token));
                        if (!CanSwitchVideo(revision, navigation, operation)) return;
                    }
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
            if (message.StartsWith("boniu:background-play-failed:", StringComparison.Ordinal))
            {
                string expected = "boniu:background-play-failed:" + navigationGeneration + ":" + backgroundRevision;
                if (backgroundMode && Visible && message == expected) ShowTransientNotice("播放未能恢复，请恢复画面检查。");
                return true;
            }
            if (!message.StartsWith("boniu:background-ended:", StringComparison.Ordinal)) return false;
            string[] parts = message.Split(':');
            int navigation; long revision; long token;
            if (parts.Length != 5 || !int.TryParse(parts[2], out navigation)
                || !long.TryParse(parts[3], out revision) || !long.TryParse(parts[4], out token)) return true;
            if (!backgroundMode || !Visible || currentIsLive || backgroundRepeatCurrent || navigation != navigationGeneration || revision != backgroundRevision) return true;
            string key = navigation.ToString(CultureInfo.InvariantCulture) + ":" + parts[4];
            if (lastAutomaticToken == key) return true;
            lastAutomaticToken = key;
            ObserveTask(SwitchVideoAsync(1, parts[4]), "AutomaticNextVideo");
            return true;
        }
    }
}
