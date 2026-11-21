// Immediate feedback for buttons that start work on the server (ActionButton/ActionIconButton): on click the
// button is greyed out and ignores further clicks right away, without waiting for the round trip to the
// server. The server's busy render (disabled + spinner) replaces the class attribute and with it this marker.
(() => {
    const pending = 'mm-pending';
    document.addEventListener('click', e => {
        const button = e.target instanceof Element ? e.target.closest('.mm-action') : null;
        if (!button || button.disabled || button.classList.contains(pending)) return;
        button.classList.add(pending);
        // Fallback in case no render comes back (e.g. the connection dropped meanwhile).
        setTimeout(() => button.classList.remove(pending), 15000);
    }, true);
})();
