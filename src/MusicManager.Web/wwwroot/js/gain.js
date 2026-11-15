// Player bar: levels the volume with the song's ReplayGain (data-gain in dB, data-peak linear) each time a
// song is loaded. The gain sits in a Web Audio node behind the <audio> element, so the volume slider stays
// the user's own. A quiet song is turned up only as far as its peak allows without clipping.
(function () {
    let context = null;
    const nodes = new WeakMap();

    window.mmGainFactor = function (gainDb, peak) {
        if (!Number.isFinite(gainDb)) return 1;
        const factor = Math.pow(10, gainDb / 20);
        const limit = Number.isFinite(peak) && peak > 0 ? 1 / peak : 1;
        return factor > 1 ? Math.max(1, Math.min(factor, limit)) : factor;
    };

    window.mmApplyGain = function (audio) {
        const factor = window.mmGainFactor(parseFloat(audio.dataset.gain), parseFloat(audio.dataset.peak));
        try {
            context ??= new AudioContext();
            let node = nodes.get(audio);
            if (!node) {
                node = context.createGain();
                context.createMediaElementSource(audio).connect(node).connect(context.destination);
                nodes.set(audio, node);
            }
            node.gain.value = factor;
        } catch {
            // No Web Audio: the element can only be turned down.
            audio.volume = Math.min(1, factor);
        }
    };

    // A context created before the first click starts suspended; without this the song would stay silent.
    window.mmResumeGain = function () {
        if (context && context.state === 'suspended') context.resume();
    };
})();
