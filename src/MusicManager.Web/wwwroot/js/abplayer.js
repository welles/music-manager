// A/B player: two <audio> elements on one timeline. Switching keeps the position, so both
// versions can be compared at the same spot. Volumes are set from the measured loudness.
window.abPlayer = (() => {
    let a = null, b = null, active = 'a';
    const current = () => (active === 'a' ? a : b);
    const other = () => (active === 'a' ? b : a);

    return {
        init(urlA, urlB) {
            this.dispose();
            a = new Audio(urlA);
            b = new Audio(urlB);
            a.preload = b.preload = 'auto';
            active = 'a';
        },
        async toggle(target) {
            if (!a || !b || target === active) return;
            const from = current();
            const wasPlaying = !from.paused;
            active = target;
            const to = current();
            to.currentTime = from.currentTime;
            from.pause();
            if (wasPlaying) await to.play();
        },
        async playPause() {
            if (!a) return;
            const c = current();
            if (c.paused) await c.play(); else c.pause();
        },
        seek(seconds) {
            if (!a) return;
            a.currentTime = Math.min(seconds, a.duration || seconds);
            b.currentTime = Math.min(seconds, b.duration || seconds);
        },
        setVolumes(va, vb) {
            if (!a) return;
            a.volume = Math.max(0, Math.min(1, va));
            b.volume = Math.max(0, Math.min(1, vb));
        },
        state() {
            if (!a) return { time: 0, duration: 0, playing: false, active };
            const c = current();
            return { time: c.currentTime || 0, duration: c.duration || 0, playing: !c.paused, active };
        },
        dispose() {
            for (const el of [a, b]) {
                if (el) { el.pause(); el.removeAttribute('src'); el.load(); }
            }
            a = b = null;
        },
    };
})();
