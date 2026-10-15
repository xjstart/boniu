using System.Globalization;

namespace MiniView.WebView2App
{
    internal static class BackgroundScripts
    {
        internal static string Build(long revision, int navigation, bool enabled, bool allowed, bool live, int volume)
        {
            return @"(() => {
                const revision = " + revision.ToString(CultureInfo.InvariantCulture) + @";
                const navigation = " + navigation.ToString(CultureInfo.InvariantCulture) + @";
                let s = window.__boniuBackground;
                if (s && revision < s.revision) return 'stale';
                if (!s) {
                    s = window.__boniuBackground = {
                        revision: -1, enabled: false, allowed: false, live: false,
                        loops: new Map(), ids: new WeakMap(), serial: 0, token: '', signature: '',
                        current: null, endedToken: '', timer: 0, observer: null, volume: 1
                    };
                    s.rendered = el => {
                        if (!el) return false;
                        const r = el.getBoundingClientRect(), cs = getComputedStyle(el);
                        return r.width > 0 && r.height > 0 && r.right > 0 && r.bottom > 0
                            && r.left < innerWidth && r.top < innerHeight && cs.display !== 'none' && cs.visibility !== 'hidden';
                    };
                    s.blocked = () => Array.from(document.querySelectorAll(
                        '[role=dialog], [data-e2e=login-modal], [class*=""login-modal""], .douyin_login_new_class')).some(s.rendered);
                    // Live cards use different markers from short videos; both belong to the same feed.
                    s.feedFor = video => video && video.closest('#slidelist, [data-e2e=""slideList""], '
                        + '[data-e2e=""feed-active-video""], [data-e2e=""feed-item""], [data-e2e=""feed-video""], [data-e2e=""feed-live""]');
                    s.area = el => {
                        const r = el.getBoundingClientRect();
                        return Math.max(0, Math.min(innerWidth,r.right)-Math.max(0,r.left))
                            * Math.max(0, Math.min(innerHeight,r.bottom)-Math.max(0,r.top));
                    };
                    s.activeItem = () => {
                        // A live feed card can render through canvas with a hidden or absent video.
                        // Navigation belongs to the slide, independently of its media renderer.
                        // Hidden WebViews stop compositing: geometry can remain on the OLD slide
                        // even after Douyin has selected and started the NEXT item. Prefer its marker.
                        const active = document.querySelector('[data-e2e=""feed-active-video""], [data-e2e=""feed-active-live""]');
                        if (active && active.getClientRects().length && getComputedStyle(active).display !== 'none')
                            return active.closest('[data-e2e=""feed-item""]') || active;
                        const playing = Array.from(document.querySelectorAll('[data-e2e=""feed-item""] video'))
                            .find(v => !v.paused && !v.ended);
                        if (playing) return playing.closest('[data-e2e=""feed-item""]');
                        for (const selector of ['[data-e2e=""feed-item""]',
                            '[data-e2e=""feed-active-video""], [data-e2e=""feed-video""], [data-e2e=""feed-live""]']) {
                            const items = Array.from(document.querySelectorAll(selector)).filter(s.rendered);
                            if (items.length) return items.sort((a,b) => s.area(b)-s.area(a))[0];
                        }
                        return null;
                    };
                    s.liveRoom = () => location.hostname === 'live.douyin.com' || /^\/live(?:\/|$)/.test(location.pathname);
                    s.isLive = () => {
                        const item = s.activeItem(), video = s.pick();
                        if (s.liveRoom()) return true;
                        const liveSelector = '[data-e2e=""feed-live""], [data-e2e=""living-container""], #PlayerLayout, .__livingPlayer__, .douyin-player';
                        if (item) return item.matches(liveSelector) || Boolean(item.querySelector(liveSelector));
                        if (video && video.closest('[data-e2e=""feed-live""], [data-e2e=""living-container""], #PlayerLayout, .__livingPlayer__')) return true;
                        // Ignore offscreen/preloaded live players and stale host detection after a feed switch.
                        return !s.feedFor(video) && Array.from(document.querySelectorAll(
                            '#PlayerLayout > .__livingPlayer__, [data-e2e=""living-container""] #PlayerLayout')).some(s.rendered);
                    };
                    s.pick = () => {
                        const item = s.activeItem();
                        if (item) {
                            const videos = item.matches('video') ? [item] : Array.from(item.querySelectorAll('video'));
                            return videos.find(s.rendered) || videos.find(v => !v.paused) || videos[0] || null;
                        }
                        const videos = Array.from(document.querySelectorAll('video')).filter(s.rendered);
                        return videos.sort((a,b) => s.area(b)-s.area(a))[0] || null;
                    };
                    s.applyVolume = () => {
                        if (s.enabled && s.current && Math.abs(s.current.volume - s.volume) > 0.001)
                            s.current.volume = s.volume;
                    };
                    s.update = () => {
                        const video = s.pick();
                        for (const [v, loop] of s.loops) {
                            if (!s.enabled || s.isLive() || v !== video || !v.isConnected) {
                                v.loop = loop; s.loops.delete(v);
                            }
                        }
                        const card = s.activeItem();
                        if (!video && !card) { s.current = null; s.token = ''; s.signature = ''; return ''; }
                        const identity = card || video;
                        if (!s.ids.has(identity)) s.ids.set(identity, ++s.serial);
                        if (video && video.srcObject && !s.ids.has(video.srcObject)) s.ids.set(video.srcObject, ++s.serial);
                        const idSelector = '[data-e2e-vid], [data-aweme-id], [data-video-id], [data-item-id], [data-room-id]';
                        const item = card ? (card.matches(idSelector) ? card : card.querySelector(idSelector)) : video.closest(idSelector);
                        // Prefer the assigned source: currentSrc changes later during load, which
                        // must not look like a second feed switch for the same media element.
                        const source = card ? '' : video.srcObject ? 'stream:' + s.ids.get(video.srcObject)
                            : video.getAttribute('src') || video.currentSrc;
                        const signature = [s.ids.get(identity), source, item ?
                            ['data-e2e-vid','data-aweme-id','data-video-id','data-item-id','data-room-id']
                                .map(name => item.getAttribute(name) || '').join(':') : ''].join('|');
                        if (signature !== s.signature) { s.signature = signature; s.token = String(++s.serial); }
                        s.current = video;
                        s.applyVolume();
                        if (video && s.enabled && !s.isLive()) {
                            if (!s.loops.has(video)) s.loops.set(video, video.loop);
                            if (video.loop) video.loop = false;
                        }
                        return s.token;
                    };
                    s.canSwitch = () => s.enabled && s.allowed && !s.blocked() && Boolean(s.update());
                    s.ended = event => {
                        if (!s.canSwitch() || s.isLive() || event.target !== s.current || s.endedToken === s.token) return;
                        const token = s.token, stamp = s.revision, nav = s.navigation;
                        s.endedToken = token;
                        clearTimeout(s.timer);
                        s.timer = setTimeout(() => {
                            if (s.revision !== stamp || !s.canSwitch() || s.isLive() || s.token !== token) return;
                            try { chrome.webview.postMessage('boniu:background-ended:' + nav + ':' + stamp + ':' + token); } catch (_) {}
                        }, 250);
                    };
                    s.loaded = event => {
                        if (event.target === s.current) s.update();
                    };
                    s.prepareKeyboard = () => {
                        if (!s.canSwitch()) return 'blocked';
                        if (!s.activeItem() && !s.feedFor(s.current)) return s.isLive() ? 'live-room' : 'unsupported';
                        const focused = document.activeElement;
                        if (focused && (focused.matches('input,textarea') || focused.isContentEditable)) focused.blur();
                        return 'keyboard';
                    };
                    s.wheelTarget = token => {
                        if (!s.canSwitch() || s.token !== token) return null;
                        const item = s.activeItem();
                        if (!item) return null;
                        const root = item.closest('#slidelist, [data-e2e=""slideList""]') || item;
                        const r = root.getBoundingClientRect();
                        const left = Math.max(0,r.left), top = Math.max(0,r.top);
                        const right = Math.min(innerWidth,r.right), bottom = Math.min(innerHeight,r.bottom);
                        if (right <= left || bottom <= top) return null;
                        return { X:(left+right)/2, Y:(top+bottom)/2 };
                    };
                    s.wheel = (direction, token) => {
                        const point = s.wheelTarget(token);
                        if (!point) return 'blocked';
                        const target = document.elementFromPoint(point.X, point.Y);
                        if (!target) return 'blocked';
                        // Native CDP mouse-wheel acknowledgments can wait indefinitely when the
                        // controller is hidden. Invoke the feed's own wheel handler synchronously.
                        target.dispatchEvent(new WheelEvent('wheel', {bubbles:true, cancelable:true,
                            view:window, clientX:point.X, clientY:point.Y, deltaX:0,
                            deltaY:direction < 0 ? -600 : 600, deltaMode:0}));
                        return 'wheel';
                    };
                    s.switchVideo = direction => {
                        if (!s.canSwitch()) return 'blocked';
                        if (s.isLive() && !s.activeItem() && !s.feedFor(s.current)) return 'live-room';
                        // Current Douyin uses these controls without aria-label/title attributes.
                        const nativeSelector = direction < 0
                            ? '[data-e2e=""video-switch-prev-arrow""], .xgplayer-playswitch-prev'
                            : '[data-e2e=""video-switch-next-arrow""], .xgplayer-playswitch-next';
                        const labels = direction < 0 ? ['上一条', '上一个视频'] : ['下一条', '下一个视频'];
                        const selectors = [nativeSelector, ...labels.map(label => '[aria-label=""' + label + '""], [title=""' + label + '""]')];
                        for (const selector of selectors) {
                            const controls = document.querySelectorAll(selector);
                            for (const control of controls) {
                                if (control.disabled || control.classList.contains('disabled') || control.getAttribute('aria-disabled') === 'true') continue;
                                control.click(); return 'native';
                            }
                        }
                        return s.prepareKeyboard();
                    };
                }
                const enabled = " + (enabled ? "true" : "false") + @";
                s.revision = revision; s.navigation = navigation;
                s.allowed = " + (allowed ? "true" : "false") + @";
                s.live = " + (live ? "true" : "false") + @";
                s.volume = " + (volume / 100.0).ToString(CultureInfo.InvariantCulture) + @";
                clearTimeout(s.timer);
                if (enabled && !s.enabled) {
                    s.enabled = true;
                    s.endedToken = '';
                    document.addEventListener('ended', s.ended, true);
                    document.addEventListener('loadstart', s.loaded, true);
                    document.addEventListener('volumechange', s.applyVolume, true);
                    s.observer = new MutationObserver(() => s.update());
                    s.observer.observe(document.documentElement, {childList: true, subtree: true,
                        attributes: true, attributeFilter: ['src', 'loop', 'data-e2e', 'data-e2e-vid', 'data-aweme-id', 'data-video-id', 'data-item-id', 'data-room-id']});
                } else if (!enabled && s.enabled) {
                    s.enabled = false;
                    document.removeEventListener('ended', s.ended, true);
                    document.removeEventListener('loadstart', s.loaded, true);
                    document.removeEventListener('volumechange', s.applyVolume, true);
                    if (s.observer) { s.observer.disconnect(); s.observer = null; }
                }
                s.update();
                return s.enabled ? 'background' : 'normal';
            })()";
        }

        internal const string Token = "window.__boniuBackground ? window.__boniuBackground.update() : ''";
        internal static readonly string LiveState = "window.__boniuBackground && window.__boniuBackground.enabled ? window.__boniuBackground.isLive() : " + PageScripts.LiveStateScript;
        internal static string Volume(long revision, int volume)
        {
            return "(()=>{const s=window.__boniuBackground;if(s&&s.enabled&&s.revision==="
                + revision.ToString(CultureInfo.InvariantCulture) + "){s.volume="
                + (volume / 100.0).ToString(CultureInfo.InvariantCulture) + ";s.update();}})()";
        }
        internal static string Switch(int direction)
        {
            return "window.__boniuBackground ? window.__boniuBackground.switchVideo(" + (direction < 0 ? "-1" : "1") + ") : 'blocked'";
        }
        internal static string Wheel(int direction, string token)
        {
            return "window.__boniuBackground ? window.__boniuBackground.wheel(" + (direction < 0 ? "-1," : "1,")
                + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(token) + ") : null";
        }
    }
}
