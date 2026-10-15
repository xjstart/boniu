using System.Globalization;

namespace MiniView.WebView2App
{
    internal static class BackgroundScripts
    {
        internal static string Build(long revision, int navigation, bool enabled, bool allowed, bool live)
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
                        current: null, endedToken: '', timer: 0, observer: null
                    };
                    s.rendered = el => {
                        if (!el) return false;
                        const r = el.getBoundingClientRect(), cs = getComputedStyle(el);
                        return r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden';
                    };
                    s.blocked = () => Array.from(document.querySelectorAll(
                        '[role=dialog], [data-e2e=login-modal], [class*=""login-modal""]')).some(s.rendered);
                    s.isLive = () => s.live || location.hostname === 'live.douyin.com'
                        || /^\/live(?:\/|$)/.test(location.pathname)
                        || Boolean(document.querySelector('#PlayerLayout > .__livingPlayer__, [data-e2e=""living-container""] #PlayerLayout'));
                    s.pick = () => {
                        const roots = s.isLive()
                            ? ['#PlayerLayout', '[data-e2e=""living-container""]']
                            : ['[data-e2e=""feed-active-video""]'];
                        for (const selector of roots) {
                            for (const root of document.querySelectorAll(selector)) {
                                const videos = root.matches('video') ? [root] : root.querySelectorAll('video');
                                for (const video of videos) if (s.rendered(video)) return video;
                            }
                        }
                        const videos = Array.from(document.querySelectorAll('video')).filter(s.rendered);
                        return videos.find(v => !v.paused && !v.ended) || videos.find(v => {
                            const r = v.getBoundingClientRect();
                            return r.left <= innerWidth / 2 && r.right >= innerWidth / 2
                                && r.top <= innerHeight / 2 && r.bottom >= innerHeight / 2;
                        }) || null;
                    };
                    s.update = () => {
                        const video = s.pick();
                        for (const [v, loop] of s.loops) {
                            if (!s.enabled || s.isLive() || v !== video || !v.isConnected) {
                                v.loop = loop; s.loops.delete(v);
                            }
                        }
                        if (!video) { s.current = null; s.token = ''; s.signature = ''; return ''; }
                        if (!s.ids.has(video)) s.ids.set(video, ++s.serial);
                        if (video.srcObject && !s.ids.has(video.srcObject)) s.ids.set(video.srcObject, ++s.serial);
                        const item = video.closest('[data-aweme-id], [data-video-id], [data-item-id]');
                        // Prefer the assigned source: currentSrc changes later during load, which
                        // must not look like a second feed switch for the same media element.
                        const source = video.srcObject ? 'stream:' + s.ids.get(video.srcObject)
                            : video.getAttribute('src') || video.currentSrc;
                        const signature = [s.ids.get(video), source, item ?
                            (item.getAttribute('data-aweme-id') || item.getAttribute('data-video-id') || item.getAttribute('data-item-id')) : ''].join('|');
                        if (signature !== s.signature) { s.signature = signature; s.token = String(++s.serial); }
                        s.current = video;
                        if (s.enabled && !s.isLive()) {
                            if (!s.loops.has(video)) s.loops.set(video, video.loop);
                            if (video.loop) video.loop = false;
                        }
                        return s.token;
                    };
                    s.canSwitch = () => s.enabled && s.allowed && !s.isLive() && !s.blocked() && Boolean(s.update());
                    s.ended = event => {
                        if (!s.canSwitch() || event.target !== s.current || s.endedToken === s.token) return;
                        const token = s.token, stamp = s.revision, nav = s.navigation;
                        s.endedToken = token;
                        clearTimeout(s.timer);
                        s.timer = setTimeout(() => {
                            if (s.revision !== stamp || !s.canSwitch() || s.token !== token) return;
                            try { chrome.webview.postMessage('boniu:background-ended:' + nav + ':' + stamp + ':' + token); } catch (_) {}
                        }, 250);
                    };
                    s.loaded = event => {
                        if (event.target === s.current) s.update();
                    };
                    s.switchVideo = direction => {
                        if (!s.canSwitch()) return 'blocked';
                        const labels = direction < 0 ? ['上一条', '上一个视频'] : ['下一条', '下一个视频'];
                        for (const label of labels) {
                            const controls = document.querySelectorAll('[aria-label=""' + label + '""], [title=""' + label + '""]');
                            for (const control of controls) {
                                if (control.disabled || control.getAttribute('aria-disabled') === 'true') continue;
                                control.click(); return 'native';
                            }
                        }
                        const feed = s.current.closest('[data-e2e=""feed-active-video""]');
                        const focused = document.activeElement;
                        if (feed && focused && (focused.matches('input,textarea') || focused.isContentEditable)) focused.blur();
                        return feed ? 'keyboard' : 'unsupported';
                    };
                }
                const enabled = " + (enabled ? "true" : "false") + @";
                s.revision = revision; s.navigation = navigation;
                s.allowed = " + (allowed ? "true" : "false") + @";
                s.live = " + (live ? "true" : "false") + @";
                clearTimeout(s.timer);
                if (enabled && !s.enabled) {
                    s.enabled = true;
                    s.endedToken = '';
                    document.addEventListener('ended', s.ended, true);
                    document.addEventListener('loadstart', s.loaded, true);
                    s.observer = new MutationObserver(() => s.update());
                    s.observer.observe(document.documentElement, {childList: true, subtree: true,
                        attributes: true, attributeFilter: ['src', 'loop', 'data-e2e', 'data-aweme-id', 'data-video-id', 'data-item-id']});
                } else if (!enabled && s.enabled) {
                    s.enabled = false;
                    document.removeEventListener('ended', s.ended, true);
                    document.removeEventListener('loadstart', s.loaded, true);
                    if (s.observer) { s.observer.disconnect(); s.observer = null; }
                }
                s.update();
                return s.enabled ? 'background' : 'normal';
            })()";
        }

        internal const string Token = "window.__boniuBackground ? window.__boniuBackground.update() : ''";
        internal static string Switch(int direction)
        {
            return "window.__boniuBackground ? window.__boniuBackground.switchVideo(" + (direction < 0 ? "-1" : "1") + ") : 'blocked'";
        }
    }
}
