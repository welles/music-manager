// Connection handling for the Blazor circuit, without user interaction where possible.
// Typical case: the PWA was in the background for a few minutes, the browser dropped the WebSocket and the
// automatic reconnect attempts ran out or the server paused the circuit. On return the app resumes or
// reloads by itself; the notice only appears when that takes longer than a moment.
const notice = document.getElementById("components-reconnect-modal");
const text = document.getElementById("reconnect-text");
const button = document.getElementById("reconnect-button");

const ShowDelayMs = 1500;
let showTimer = null;
let busy = false;

notice.addEventListener("components-reconnect-state-changed", event => {
    switch (event.detail.state) {
        case "show":
        case "retrying":
            working();
            break;
        case "hide":
            hide();
            break;
        case "paused":
        case "resume-failed":
        case "failed":
            whenVisible(recover);
            break;
        case "rejected":
            location.reload();
            break;
    }
});

button.addEventListener("click", () => location.reload());

/** Reconnect to the circuit; if the server no longer has it, resume from the persisted state or reload. */
async function recover() {
    if (busy) return;
    busy = true;
    working();
    try {
        // true = reconnected, false = server reached but the circuit is gone, exception = server unreachable.
        const ok = await Blazor.reconnect();
        if (!ok && !(await Blazor.resumeCircuit())) {
            location.reload();
            return;
        }
        hide();
    } catch {
        offline();
    } finally {
        busy = false;
    }
}

function whenVisible(action) {
    if (document.visibilityState === "visible") {
        action();
        return;
    }
    const onChange = () => {
        if (document.visibilityState !== "visible") return;
        document.removeEventListener("visibilitychange", onChange);
        action();
    };
    document.addEventListener("visibilitychange", onChange);
}

function working() {
    text.textContent = "Verbindung wird wiederhergestellt …";
    button.hidden = true;
    notice.classList.remove("reconnect-offline");
    if (!showTimer && !notice.classList.contains("reconnect-visible")) {
        showTimer = setTimeout(() => notice.classList.add("reconnect-visible"), ShowDelayMs);
    }
}

function offline() {
    clearTimeout(showTimer);
    showTimer = null;
    text.textContent = "Server nicht erreichbar";
    button.hidden = false;
    notice.classList.add("reconnect-visible", "reconnect-offline");
    // Keep trying while the app is open (e.g. the server restarts after an update).
    setTimeout(() => whenVisible(recover), 5000);
}

function hide() {
    clearTimeout(showTimer);
    showTimer = null;
    notice.classList.remove("reconnect-visible", "reconnect-offline");
}
