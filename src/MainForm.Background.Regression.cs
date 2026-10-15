using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace MiniView.WebView2App
{
    internal sealed partial class MainForm
    {
        private readonly bool backgroundRegressionOnly;
        private async Task RunBackgroundRegressionAsync()
        {
            BeginShortcutCapture("previous");
            CaptureShortcutKey(this, new KeyEventArgs(Keys.Control | Keys.Alt | Keys.Down));
            AssertRegression(captureTarget == "previous" && previousVideoShortcutStatus.Text.Contains("相同"), "video hotkeys reject conflicts with each other");
            CaptureShortcutKey(this, new KeyEventArgs(Keys.Control | Keys.Alt | Keys.D));
            AssertRegression(captureTarget == "previous", "video hotkeys reject conflicts with boss key");
            CancelShortcutCapture();
            BeginShortcutCapture("previous");
            CaptureShortcutKey(this, new KeyEventArgs(Keys.Delete));
            AssertRegression(previousVideoHotkey == null && settings.PreviousVideoShortcut == "", "video shortcut can be disabled");
            BeginShortcutCapture("previous");
            CaptureShortcutKey(this, new KeyEventArgs(Keys.Control | Keys.Shift | Keys.Up));
            AssertRegression(previousVideoHotkey.Serialize() == "Control+Shift+Up" && settings.PreviousVideoShortcut == "Control+Shift+Up", "custom video shortcut captured and persisted");
            BeginShortcutCapture("previous");
            CaptureShortcutKey(this, new KeyEventArgs(Keys.Control | Keys.Alt | Keys.Up));
            Rectangle normal = Bounds;
            Size viewport = webView.Size;
            double zoom = webView.ZoomFactor;
            ToggleBackgroundMode();
            await WaitForPageAsync("window.__boniuBackground && window.__boniuBackground.enabled && !document.getElementById('playing').loop");
            AssertRegression(backgroundMode && !webView.Visible && backgroundPanel.Visible && toolbar.Visible, "background hides picture and keeps controls");
            AssertRegression(webView.Size == viewport && webView.ZoomFactor == zoom, "background retains original viewport and zoom");
            AssertRegression(Width == Scaled(360) && Height == Scaled(160), "background default compact dimensions");
            using (Bitmap image = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                DrawToBitmap(image, ClientRectangle);
                Directory.CreateDirectory(appFolder);
                image.Save(Path.Combine(appFolder, "background-fixture.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            AssertRegression(BackgroundLayoutAcrossScales(), "background controls fit minimum window at 100/125/150/200 percent DPI");
            await webView.ExecuteScriptAsync("window.backgroundTime=document.getElementById('playing').currentTime");
            await Task.Delay(350);
            AssertRegression(await PageIsTrueAsync("document.getElementById('playing').currentTime > window.backgroundTime && document.getElementById('manual').paused"), "hidden picture continues playing; manual pause preserved");
            settings.HideWhenInactive = true;
            HideWhenApplicationBecomesInactive();
            AssertRegression(Visible, "background survives application focus loss");
            settings.HideWhenInactive = false;
            await SuspendWebViewIfHiddenAsync();
            AssertRegression(!webViewSuspended && !suspendTimer.Enabled, "active background playback never suspends");

            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 2 && window.fixtureSwitches === 1"), "next uses native feed control");
            await SwitchVideoAsync(-1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 1 && window.fixtureSwitches === 2"), "previous uses native feed control");
            await webView.ExecuteScriptAsync("window.fixtureReuse=true");
            string oldToken = await ReadBackgroundTokenAsync();
            await SwitchVideoAsync(1, null);
            AssertRegression(await ReadBackgroundTokenAsync() != oldToken, "recycled video element gets a new feed identity");
            string loadedToken = await ReadBackgroundTokenAsync();
            await webView.ExecuteScriptAsync("document.getElementById('playing').dispatchEvent(new Event('loadstart'))");
            AssertRegression(await ReadBackgroundTokenAsync() == loadedToken, "load notifications do not count as a second feed switch");
            await webView.ExecuteScriptAsync("document.getElementById('playing').dispatchEvent(new Event('ended'));document.getElementById('playing').dispatchEvent(new Event('ended'))");
            await WaitForPageAsync("window.fixtureIndex === 3");
            await WaitForConditionAsync(delegate { return !videoSwitchPending; }, "automatic switch completes", 5000);
            await Task.Delay(350);
            AssertRegression(await PageIsTrueAsync("window.fixtureSwitches === 4"), "duplicate ended events advance exactly once");

            await webView.ExecuteScriptAsync("document.getElementById('playing').dispatchEvent(new Event('ended'));window.fixtureSwitch(1)");
            await Task.Delay(500);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 4 && window.fixtureSwitches === 5"), "native autoadvance is not repeated by host");
            string staleToken = await ReadBackgroundTokenAsync();
            long staleRevision = backgroundRevision;
            HideWindow();
            AssertRegression(!Visible && webView.CoreWebView2.IsMuted, "boss key immediately mutes background audio");
            await WaitForPageAsync("document.getElementById('playing').paused && !window.__boniuBackground.allowed");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 4"), "hidden window ignores video hotkeys");
            ShowWindow();
            await WaitForPageAsync("!document.getElementById('playing').paused && window.__boniuBackground.allowed");
            AssertRegression(backgroundMode && !webView.Visible && !webView.CoreWebView2.IsMuted, "show restores audio controls without exposing picture");
            HandleBackgroundMessage("boniu:background-ended:" + navigationGeneration + ":" + staleRevision + ":" + staleToken);
            await Task.Delay(350);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 4"), "stale background ended message rejected");
            await webView.ExecuteScriptAsync("document.getElementById('playing').pause()");
            HideWindow();
            ShowWindow();
            await Task.Delay(350);
            AssertRegression(await PageIsTrueAsync("document.getElementById('playing').paused"), "manual pause survives background hide and show");
            await webView.ExecuteScriptAsync("document.getElementById('playing').play()");
            OnSessionSwitch(this, new SessionSwitchEventArgs(SessionSwitchReason.SessionLock));
            await WaitForConditionAsync(delegate { return !Visible; }, "session lock hides background", 3000);
            await WaitForPageAsync("document.getElementById('playing').paused");
            AssertRegression(webView.CoreWebView2.IsMuted, "session lock immediately mutes and pauses background playback");
            ShowWindow();
            await WaitForPageAsync("!document.getElementById('playing').paused && window.__boniuBackground.allowed");

            Rectangle compact = Bounds;
            for (int i = 0; i < 3; i++)
            {
                ToggleSettings();
                AssertRegression(settingsOpen && !webView.Visible && toolbar.Visible && Height >= Scaled(460), "background settings expanded-" + i);
                NavigateBack();
                AssertRegression(!settingsOpen && !webView.Visible && Bounds == compact, "background settings return compact-" + i);
            }
            settings.ChromeHidden = true;
            settings.ImmersiveMode = true;
            ApplyChromeState(true);
            AssertRegression(toolbar.Visible && !ToolbarCollapsed && !webView.Visible, "background retains controls with hidden chrome and immersive settings");
            await webView.ExecuteScriptAsync("const live=document.createElement('div');live.id='PlayerLayout';live.innerHTML='<div class=\"__livingPlayer__\"></div>';document.body.appendChild(live)");
            await DetectLiveStateAsync();
            await WaitForPageAsync("window.__boniuBackground.live");
            AssertRegression(!previousVideoButton.Enabled && !nextVideoButton.Enabled && backgroundPlaybackStatus.Text.Contains("直播"), "live background disables video navigation");
            AssertRegression(Bounds == compact, "live detection does not resize background controls");
            await SwitchVideoAsync(1, null);
            await webView.ExecuteScriptAsync("document.getElementById('playing').dispatchEvent(new Event('ended'))");
            await Task.Delay(350);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 4"), "live background never auto advances");
            await webView.ExecuteScriptAsync("document.getElementById('PlayerLayout').remove()");
            await DetectLiveStateAsync();
            await WaitForPageAsync("!window.__boniuBackground.live");

            await webView.ExecuteScriptAsync("const d=document.createElement('div');d.setAttribute('role','dialog');d.id='blocked-dialog';d.style.cssText='position:fixed;width:100px;height:100px';document.body.appendChild(d)");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 4") && !string.IsNullOrEmpty(transientNotice), "dialog blocks navigation and shows inline notice");
            await webView.ExecuteScriptAsync("document.getElementById('blocked-dialog').remove();document.querySelectorAll('button').forEach(b=>b.remove());document.addEventListener('keydown',e=>{if(e.key==='ArrowDown')window.fixtureSwitch(1);if(e.key==='ArrowUp')window.fixtureSwitch(-1)})");
            await webView.ExecuteScriptAsync("document.getElementById('playing').style.display='none'");
            await SwitchVideoAsync(1, null);
            AssertRegression(transientNotice.Contains("没有") && await PageIsTrueAsync("window.fixtureIndex === 4"), "empty video page shows recoverable notice without switching");
            await webView.ExecuteScriptAsync("document.getElementById('playing').style.display=''");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 5"), "CDP key fallback switches within unfocused hidden WebView");
            await webView.ExecuteScriptAsync("document.querySelector('[data-e2e]').removeAttribute('data-e2e')");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 5") && transientNotice.Contains("不支持"), "unsupported page fails once without retries");
            await webView.ExecuteScriptAsync("document.getElementById('playing').parentElement.setAttribute('data-e2e','feed-active-video')");

            // Record a short local clip: actual decoding/ended verifies the loop override and
            // automatic next path, without relying only on synthetic DOM events or the network.
            settings.Muted = true;
            await ApplyMediaStateAsync(); // Mute at the host so the audio-bearing fixture stays silent.
            await webView.ExecuteScriptAsync(@"(async()=>{
                const audio=new AudioContext(),oscillator=audio.createOscillator(),output=audio.createMediaStreamDestination();
                oscillator.connect(output);oscillator.start();
                const recordedStream=new MediaStream([...stream.getVideoTracks(),...output.stream.getAudioTracks()]);
                const chunks=[],recorder=new MediaRecorder(recordedStream);
                const clip=new Promise(resolve=>{recorder.ondataavailable=e=>chunks.push(e.data);
                    recorder.onstop=()=>resolve(new Blob(chunks,{type:recorder.mimeType}));});
                recorder.start();setTimeout(()=>recorder.stop(),650);
                const blob=await clip,v=document.getElementById('playing');
                window.naturalEnded=0;v.addEventListener('ended',()=>window.naturalEnded++);
                v.pause();v.srcObject=null;v.src=URL.createObjectURL(blob);v.loop=true;v.muted=false;await v.play();
                window.clipPlayResolved=true;
                oscillator.stop();audio.close();
            })().catch(e=>window.clipError=String(e))");
            for (int i = 0; i < 100 && !await PageIsTrueAsync("window.naturalEnded === 1 && window.fixtureIndex === 6"); i++) await Task.Delay(100);
            if (!await PageIsTrueAsync("window.naturalEnded === 1 && window.fixtureIndex === 6"))
            {
                Console.Error.WriteLine("Natural ended fixture: " + await webView.ExecuteScriptAsync(@"(()=>{
                    const v=document.getElementById('playing'),s=window.__boniuBackground;
                    return {index:window.fixtureIndex,switches:window.fixtureSwitches,ended:window.naturalEnded,
                        paused:v.paused,time:v.currentTime,duration:v.duration,loop:v.loop,ready:v.readyState,
                        allowed:s.allowed,live:s.live,token:s.token,endedToken:s.endedToken,revision:s.revision,
                        error:window.clipError,playResolved:window.clipPlayResolved};})()"));
                throw new InvalidOperationException("Natural video ended and advance");
            }
            await WaitForConditionAsync(delegate { return !videoSwitchPending; }, "natural ended switch completes", 5000);
            AssertRegression(await PageIsTrueAsync("window.fixtureSwitches === 7 && !document.getElementById('playing').paused"), "finite local video ends and automatically advances once");
            settings.Muted = false;
            await ApplyMediaStateAsync();

            await webView.ExecuteScriptAsync("const b=document.createElement('button');b.setAttribute('aria-label','下一条');b.id='no-op-next';document.body.appendChild(b)");
            Task switching = SwitchVideoAsync(1, null);
            await SwitchVideoAsync(1, null);
            await switching;
            AssertRegression(!videoSwitchPending && transientNotice.Contains("未完成") && await PageIsTrueAsync("window.fixtureIndex === 6"), "duplicate requests merge and stalled switch times out once");
            await webView.ExecuteScriptAsync("document.getElementById('no-op-next').remove()");

            settings.ChromeHidden = false;
            settings.ImmersiveMode = false;
            ToggleBackgroundMode();
            await WaitForPageAsync("!window.__boniuBackground.enabled && document.getElementById('playing').loop");
            AssertRegression(Bounds == normal && webView.Dock == DockStyle.Fill && webView.Visible, "exit background restores window and WebView");
            AssertRegression(await PageIsTrueAsync("window.__boniuBackground.loops.size===0 && !window.__boniuBackground.observer"), "exit restores loops and removes page observers");
            AssertRegression(await BackgroundPictureRendersAsync(), "decoded picture and visible controller restored after background exit");
            ClearTransientNotice();

            for (int i = 0; i < 5; i++)
            {
                ToggleBackgroundMode();
                ToggleBackgroundMode();
            }
            await WaitForPageAsync("!window.__boniuBackground.enabled && document.getElementById('playing').loop");
            AssertRegression(Bounds == normal && await BackgroundPictureRendersAsync(), "rapid background toggles preserve final picture and normal bounds");

            // Navigation must install a fresh observer without reusing prior video identities.
            ToggleBackgroundMode();
            webView.NavigateToString(RegressionHtml);
            await WaitForPageAsync("window.fixtureReady && window.__boniuBackground && window.__boniuBackground.enabled");
            AssertRegression(backgroundMode && !webView.Visible, "navigation keeps background picture hidden");
            ToggleBackgroundMode();
            await WaitForPageAsync("window.__boniuBackground && !window.__boniuBackground.enabled");
            AssertRegression(await BackgroundPictureRendersAsync(), "picture restores after navigation in background");
        }

        private async Task<bool> BackgroundPictureRendersAsync()
        {
            // Independently verify native visibility + decoded preview for background scenarios.
            // The general regression additionally requires real desktop pixels on both architectures;
            // preview success never makes an unavailable desktop capture pass the full suite.
            if (!Visible || !webView.Visible || !NativeMethods.IsWindowVisible(Handle)) return false;
            for (int i = 0; i < 15; i++)
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    await webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                    stream.Position = 0;
                    using (Bitmap bitmap = new Bitmap(stream))
                    {
                        Color color = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
                        if (NearColor(color, Color.FromArgb(22, 125, 76)) || NearColor(color, Color.FromArgb(34, 102, 170))) return true;
                    }
                }
                await Task.Delay(100);
            }
            return false;
        }

        private bool BackgroundLayoutAcrossScales()
        {
            float originalScale = uiScale;
            Rectangle originalBounds = Bounds;
            Size originalMinimum = MinimumSize;
            BoundsData savedBackground = settings.BackgroundBounds;
            bool valid = true;
            foreach (float scale in new[] { 1F, 1.25F, 1.5F, 2F })
            {
                uiScale = scale;
                MinimumSize = new Size(Scaled(280), Scaled(160));
                SetBoundsProgrammatically(new Rectangle(Left, Top, Scaled(280), Scaled(160)));
                LayoutWindow();
                foreach (Control button in new Control[] { previousVideoButton, nextVideoButton, backgroundMuteButton, restoreVideoButton })
                    valid &= backgroundPanel.ClientRectangle.Contains(button.Bounds) && button.Width >= Scaled(100);
                valid &= toolbar.Visible && !ToolbarCollapsed && toolbar.ClientRectangle.Contains(settingsButton.Bounds)
                    && toolbar.ClientRectangle.Contains(hideButton.Bounds) && toolbar.ClientRectangle.Contains(closeButton.Bounds);
            }
            uiScale = originalScale;
            MinimumSize = originalMinimum;
            SetBoundsProgrammatically(originalBounds);
            settings.BackgroundBounds = savedBackground;
            LayoutWindow();
            return valid;
        }
    }
}
