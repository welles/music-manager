// Service worker of the Music Manager PWA: makes the app installable and shows push notifications.
// No offline cache – Blazor Server needs the connection to the server anyway.

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

self.addEventListener('push', event => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch { data = { body: event.data?.text() }; }

    event.waitUntil(self.registration.showNotification(data.title || 'Music Manager', {
        body: data.body || '',
        tag: data.tag || 'music-manager',
        // Replaces the previous summary but still alerts the user.
        renotify: true,
        icon: 'icons/icon-192.png',
        badge: 'icons/badge-96.png',
        data: { url: data.url || '/' },
    }));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = new URL(event.notification.data?.url || '/', self.registration.scope).href;

    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const client of windows) {
            if (new URL(client.url).origin === self.location.origin && 'focus' in client) {
                await client.focus();
                if ('navigate' in client) await client.navigate(url);
                return;
            }
        }
        await self.clients.openWindow(url);
    })());
});
