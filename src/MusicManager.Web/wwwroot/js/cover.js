// Cover images of SongCard: when a cover cannot be loaded, try the next URL of data-fallbacks
// ("url|url|placeholder"), e.g. Cover Art Archive release -> release group -> placeholder.
window.mmCoverFallback = function (img) {
    const rest = (img.dataset.fallbacks || '').split('|').filter(Boolean);
    const next = rest.shift();
    img.dataset.fallbacks = rest.join('|');
    if (next) img.src = next;
    else img.onerror = null;
};
