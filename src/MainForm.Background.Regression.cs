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
            AssertRegression(Width == Scaled(360) && Height == Scaled(200), "background default compact dimensions");
            using (Bitmap image = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                DrawToBitmap(image, ClientRectangle);
                Directory.CreateDirectory(appFolder);
                image.Save(Path.Combine(appFolder, "background-fixture.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            AssertRegression(BackgroundLayoutAcrossScales(), "background controls fit minimum window at 100/125/150/200 percent DPI");
            navigationFailed = true;
            RefreshPageNotice();
            AssertRegression(pageNotice.Visible && !pageNotice.Bounds.IntersectsWith(previousVideoButton.Bounds), "background navigation failure remains actionable without covering controls");
            navigationFailed = false;
            RefreshPageNotice();
            await webView.ExecuteScriptAsync("window.backgroundTime=document.getElementById('playing').currentTime");
            await Task.Delay(350);
            AssertRegression(await PageIsTrueAsync("document.getElementById('playing').currentTime > window.backgroundTime && document.getElementById('manual').paused"), "hidden picture continues playing; manual pause preserved");
            settings.HideWhenInactive = true;
            HideWhenApplicationBecomesInactive();
            AssertRegression(Visible, "background survives application focus loss");
            settings.HideWhenInactive = false;
            await SuspendWebViewIfHiddenAsync();
            AssertRegression(!webViewSuspended && !suspendTimer.Enabled, "active background playback never suspends");

            backgroundVolumeSlider.Value = 35;
            await WaitForPageAsync("Math.abs(document.getElementById('playing').volume-0.35)<0.001");
            AssertRegression(settings.BackgroundVolume == 35 && backgroundVolumeLabel.Text.Contains("35%"), "slider sets media volume and saved preference");
            backgroundVolumeSlider.Value = 0;
            await WaitForPageAsync("document.getElementById('playing').volume===0");
            AssertRegression(await PageIsTrueAsync("!document.getElementById('playing').paused && document.getElementById('manual').paused"), "zero volume does not change playback or manual pause");
            ToggleMute();
            backgroundVolumeSlider.Value = 35;
            await WaitForPageAsync("Math.abs(document.getElementById('playing').volume-0.35)<0.001");
            AssertRegression(webView.CoreWebView2.IsMuted, "volume adjustment preserves explicit host mute");
            ToggleMute();

            await webView.ExecuteScriptAsync("document.querySelector('[aria-label=上一条]').className='xgplayer-playswitch-prev';document.querySelector('[aria-label=下一条]').className='xgplayer-playswitch-next';document.querySelectorAll('button[aria-label]').forEach(b=>b.removeAttribute('aria-label'))");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 2 && window.fixtureSwitches === 1"), "next uses real Douyin native control without aria label");
            await WaitForPageAsync("Math.abs(document.getElementById('playing').volume-0.35)<0.001");
            AssertRegression(await PageIsTrueAsync("document.getElementById('playing').volume<0.36"), "replacement video inherits background volume");
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
            AssertRegression(await PageIsTrueAsync("Math.abs(document.getElementById('playing').volume-0.35)<0.001"), "boss hide and restore preserve volume");
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
            await webView.ExecuteScriptAsync("const live=document.getElementById('playing').parentElement;live.setAttribute('data-e2e','feed-live');live.style.cssText='width:100%;height:100%'");
            await DetectLiveStateAsync();
            await WaitForPageAsync("window.__boniuBackground.live");
            AssertRegression(previousVideoButton.Enabled && nextVideoButton.Enabled && backgroundPlaybackStatus.Text.Contains("直播"), "feed live card keeps manual video navigation");
            AssertRegression(Bounds == compact, "live detection does not resize background controls");
            await webView.ExecuteScriptAsync("document.getElementById('playing').dispatchEvent(new Event('ended'))");
            await Task.Delay(350);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 4"), "live background never auto advances");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 5"), "native next leaves a live feed card");
            await SwitchVideoAsync(-1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 4"), "native previous leaves a live feed card");
            await webView.ExecuteScriptAsync("document.getElementById('playing').parentElement.setAttribute('data-e2e','feed-active-video')");
            await DetectLiveStateAsync();
            await WaitForPageAsync("!window.__boniuBackground.live");
            await webView.ExecuteScriptAsync("const cached=document.createElement('div');cached.id='cached-live';cached.setAttribute('data-e2e','feed-live');cached.style.cssText='position:fixed;top:200vh;width:100%;height:100%';const v=document.createElement('video');v.style.cssText='width:100%;height:100%';cached.appendChild(v);document.body.prepend(cached)");
            AssertRegression(await PageIsTrueAsync("!window.__boniuBackground.isLive() && window.__boniuBackground.pick().id==='playing'"), "offscreen preloaded live card does not replace active short video");
            await webView.ExecuteScriptAsync("document.getElementById('cached-live').remove()");

            await webView.ExecuteScriptAsync(@"window.canvasVideo=document.getElementById('playing');window.canvasCard=canvasVideo.parentElement;
                canvasCard.setAttribute('data-e2e','feed-item');canvasVideo.pause();canvasVideo.remove();
                window.canvasRenderer=document.createElement('canvas');canvasRenderer.className='douyin-player';canvasCard.appendChild(canvasRenderer);
                window.savedFixtureSwitch=window.fixtureSwitch;
                window.fixtureSwitch=direction=>{window.fixtureIndex+=direction;window.canvasCard.setAttribute('data-room-id',String(window.fixtureIndex));};");
            await DetectLiveStateAsync();
            await WaitForPageAsync("window.__boniuBackground.live");
            string canvasToken = await ReadBackgroundTokenAsync();
            AssertRegression(!string.IsNullOrEmpty(canvasToken) && await PageIsTrueAsync("!window.__boniuBackground.current"), "canvas live card has navigation identity without a video element");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex===5") && await ReadBackgroundTokenAsync() != canvasToken, "canvas live next confirms recycled card room identity");
            await SwitchVideoAsync(-1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex===4"), "canvas live previous remains available");
            await webView.ExecuteScriptAsync("canvasRenderer.remove();canvasCard.removeAttribute('data-room-id');canvasCard.setAttribute('data-e2e','feed-active-video');canvasCard.appendChild(canvasVideo);window.fixtureSwitch=window.savedFixtureSwitch;canvasVideo.play()");
            await DetectLiveStateAsync();
            await WaitForPageAsync("!window.__boniuBackground.live");
            string beforeFrozenSwitch = await ReadBackgroundTokenAsync();
            await webView.ExecuteScriptAsync(@"canvasCard.setAttribute('data-e2e','feed-video');
                const next=document.createElement('div');next.id='frozen-next';next.setAttribute('data-e2e','feed-active-live');next.setAttribute('data-room-id','next-room');
                next.style.cssText='position:fixed;top:200vh;width:100px;height:100px';next.innerHTML='<canvas class=douyin-player></canvas>';document.body.appendChild(next)");
            AssertRegression(await ReadBackgroundTokenAsync() != beforeFrozenSwitch
                && await PageIsTrueAsync("window.__boniuBackground.isLive() && !window.__boniuBackground.current"),
                "logical active live card wins when hidden compositor leaves previous video on screen");
            await webView.ExecuteScriptAsync("document.getElementById('frozen-next').remove();canvasCard.setAttribute('data-e2e','feed-active-video')");
            await DetectLiveStateAsync();
            await WaitForPageAsync("!window.__boniuBackground.live");

            await webView.ExecuteScriptAsync("const d=document.createElement('div');d.setAttribute('role','dialog');d.id='blocked-dialog';d.style.cssText='position:fixed;top:0;left:0;width:100px;height:100px';document.body.appendChild(d)");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 4") && !string.IsNullOrEmpty(transientNotice), "dialog blocks navigation and shows inline notice");
            AssertRegression(!pageNotice.Visible && backgroundPlaybackStatus.Text == transientNotice, "background notice occupies status row without covering buttons");
            await webView.ExecuteScriptAsync("document.getElementById('blocked-dialog').removeAttribute('role');document.getElementById('blocked-dialog').className='douyin_login_new_class'");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex===4") && transientNotice.Contains("弹窗"), "current Douyin login overlay without dialog role blocks navigation");
            await webView.ExecuteScriptAsync("document.getElementById('blocked-dialog').remove();document.querySelectorAll('button').forEach(b=>b.remove());document.addEventListener('keydown',e=>{if(e.key==='ArrowDown')window.fixtureSwitch(1);if(e.key==='ArrowUp')window.fixtureSwitch(-1)})");
            await webView.ExecuteScriptAsync("document.getElementById('playing').parentElement.style.display='none'");
            await SwitchVideoAsync(1, null);
            AssertRegression(transientNotice.Contains("没有") && await PageIsTrueAsync("window.fixtureIndex === 4"), "empty video page shows recoverable notice without switching");
            await webView.ExecuteScriptAsync("document.getElementById('playing').parentElement.style.display=''");
            await webView.ExecuteScriptAsync("document.getElementById('playing').parentElement.setAttribute('data-e2e','feed-live')");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex === 5"), "CDP key fallback switches live card without feed-active-video marker");
            await webView.ExecuteScriptAsync("document.getElementById('playing').parentElement.id='PlayerLayout';document.querySelector('[data-e2e]').removeAttribute('data-e2e')");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex===5") && transientNotice.Contains("独立直播间"), "standalone live room gives actionable notice instead of generic unsupported error");
            await webView.ExecuteScriptAsync("document.getElementById('PlayerLayout').removeAttribute('id');document.getElementById('playing').parentElement.setAttribute('data-e2e','feed-active-video')");
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
            AssertRegression(await PageIsTrueAsync("window.fixtureSwitches === 9 && !document.getElementById('playing').paused"), "finite local video ends and automatically advances once");
            settings.Muted = false;
            await ApplyMediaStateAsync();

            await webView.ExecuteScriptAsync("const b=document.createElement('button');b.setAttribute('aria-label','下一条');b.id='no-op-next';document.body.appendChild(b)");
            Task switching = SwitchVideoAsync(1, null);
            await SwitchVideoAsync(1, null);
            await switching;
            AssertRegression(!videoSwitchPending && transientNotice.Contains("未完成") && await PageIsTrueAsync("window.fixtureIndex === 6"), "duplicate requests merge and stalled switch times out once");
            await webView.ExecuteScriptAsync("window.fixtureWheels=0;document.addEventListener('wheel',e=>{window.fixtureWheels++;window.fixtureSwitch(e.deltaY>0?1:-1)},{once:true})");
            await SwitchVideoAsync(1, null);
            AssertRegression(await PageIsTrueAsync("window.fixtureIndex===7 && window.fixtureWheels===1") && string.IsNullOrEmpty(transientNotice),
                "unresponsive native switch falls back to one page-local wheel event");
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
            await WaitForPageAsync("Math.abs(document.getElementById('playing').volume-0.35)<0.001");
            AssertRegression(backgroundVolumeSlider.Value == 35, "volume survives mode toggles and navigation");
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
                MinimumSize = new Size(Scaled(280), Scaled(200));
                SetBoundsProgrammatically(new Rectangle(Left, Top, Scaled(280), Scaled(200)));
                LayoutWindow();
                foreach (Control button in new Control[] { previousVideoButton, nextVideoButton, backgroundMuteButton, restoreVideoButton })
                    valid &= backgroundPanel.ClientRectangle.Contains(button.Bounds) && button.Width >= Scaled(100);
                valid &= backgroundPanel.ClientRectangle.Contains(backgroundVolumeSlider.Bounds)
                    && backgroundPanel.ClientRectangle.Contains(backgroundVolumeLabel.Bounds);
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
