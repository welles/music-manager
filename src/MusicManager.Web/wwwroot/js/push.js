// Registers the service worker and manages the Web Push subscription of this browser.
(() => {
    const registration = 'serviceWorker' in navigator
        ? navigator.serviceWorker.register('sw.js', { scope: '/' }).catch(err => { console.warn('Service worker registration failed', err); return null; })
        : Promise.resolve(null);

    function base64UrlToBytes(value) {
        const padded = (value + '='.repeat((4 - value.length % 4) % 4)).replace(/-/g, '+').replace(/_/g, '/');
        return Uint8Array.from(atob(padded), c => c.charCodeAt(0));
    }

    function toDto(subscription) {
        const json = subscription.toJSON();
        return { endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth, device: describeDevice() };
    }

    function describeDevice() {
        const ua = navigator.userAgent;
        const os = /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Windows/.test(ua) ? 'Windows'
            : /Mac OS/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : 'Unbekannt';
        const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome'
            : /Safari\//.test(ua) ? 'Safari' : 'Browser';
        const installed = window.matchMedia('(display-mode: standalone)').matches ? ' (App)' : '';
        return `${browser} auf ${os}${installed}`;
    }

    window.mmPush = {
        /** "unsupported", "denied", "default" or "granted". */
        state() {
            if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) return 'unsupported';
            return Notification.permission;
        },

        /** Endpoint of this browser's current subscription, or null. */
        async current() {
            const reg = await registration;
            const sub = reg ? await reg.pushManager.getSubscription() : null;
            return sub ? sub.endpoint : null;
        },

        /** Asks for permission and subscribes with the server's VAPID key; returns the subscription or null. */
        async subscribe(publicKey) {
            const reg = await registration;
            if (!reg) return null;
            if (await Notification.requestPermission() !== 'granted') return null;
            await navigator.serviceWorker.ready;

            let sub = await reg.pushManager.getSubscription();
            // A subscription made with another server key cannot receive our messages.
            if (sub && sub.options.applicationServerKey) {
                const existing = new Uint8Array(sub.options.applicationServerKey);
                const wanted = base64UrlToBytes(publicKey);
                if (existing.length !== wanted.length || existing.some((b, i) => b !== wanted[i])) {
                    await sub.unsubscribe();
                    sub = null;
                }
            }
            sub ??= await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: base64UrlToBytes(publicKey) });
            return toDto(sub);
        },

        /** Removes this browser's subscription; returns its endpoint or null. */
        async unsubscribe() {
            const reg = await registration;
            const sub = reg ? await reg.pushManager.getSubscription() : null;
            if (!sub) return null;
            const endpoint = sub.endpoint;
            await sub.unsubscribe();
            return endpoint;
        },
    };
})();
