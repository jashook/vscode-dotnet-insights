// Script run within the webview itself - drives the .nettrace loading
// placeholder (see NettraceLoadingRenderer.ts) shown immediately when a
// .nettrace file is opened, before nettraceParser has even been spawned.
// Own tiny script rather than reusing media/snapshotGcStats.js's globals -
// this is a completely separate webview document (see
// NettraceLoadingRenderer.ts's own header comment on why), so there's
// nothing to share.

(function () {
    const vscode = acquireVsCodeApi();

    const fileNameJson = document.getElementById('nettraceLoadingFileNameJson');
    if (fileNameJson) {
        try {
            document.getElementById('nettraceLoadingFileName').textContent = JSON.parse(fileNameJson.textContent);
        } catch (parseError) {
            // Not worth failing the whole placeholder over a cosmetic title.
        }
    }

    const pieRingElement = document.getElementById('nettraceLoadingPieRing');
    const percentElement = document.getElementById('nettraceLoadingPercent');
    const labelElement = document.getElementById('nettraceLoadingLabel');
    const errorElement = document.getElementById('nettraceLoadingError');
    const elapsedElement = document.getElementById('nettraceLoadingElapsed');

    // Elapsed time, recomputed from a start timestamp on every tick rather
    // than by incrementing a counter. setInterval makes no guarantee it
    // fires exactly once a second - and a hidden/backgrounded webview gets
    // its timers throttled hard - so a tick counter would silently drift
    // slow and under-report precisely during a long parse, which is the
    // only time anyone reads this. Subtracting timestamps stays correct no
    // matter how many ticks were coalesced or dropped.
    const startedAtMSec = Date.now();
    let elapsedIntervalId = null;

    function formatElapsed(totalSeconds) {
        const seconds = totalSeconds % 60;
        const totalMinutes = Math.floor(totalSeconds / 60);
        const minutes = totalMinutes % 60;
        const hours = Math.floor(totalMinutes / 60);

        const paddedSeconds = String(seconds).padStart(2, '0');

        // Hours only once there are any - a parse that long is pathological,
        // but "1:02:03" beats "62:03" if it ever happens.
        if (hours > 0) {
            return hours + ':' + String(minutes).padStart(2, '0') + ':' + paddedSeconds;
        }

        return minutes + ':' + paddedSeconds;
    }

    function updateElapsed() {
        elapsedElement.textContent = formatElapsed(Math.floor((Date.now() - startedAtMSec) / 1000));
    }

    function stopElapsed() {
        if (elapsedIntervalId !== null) {
            clearInterval(elapsedIntervalId);
            elapsedIntervalId = null;
        }
    }

    // No stop on success: this whole document is replaced wholesale once
    // parsing finishes (see NettraceLoadingRenderer.ts's own header comment
    // on why it's a swap rather than an in-place patch), which tears the
    // timer down with it.
    elapsedIntervalId = setInterval(updateElapsed, 1000);

    // The ring's own "pie chart" fill - a conic-gradient wedge growing
    // clockwise from 12 o'clock to fill the circle as percent approaches
    // 100. Set directly here (inline style, not a CSS custom property fed
    // through calc()) so the exact same value driving the visual wedge is
    // also what's shown as text - one source of truth, no risk of the two
    // drifting apart. message.percent is always already a whole number by
    // the time it gets here (see NettraceProgress.ts's own
    // NettraceProgressTracker.record, which rounds before this is ever
    // posted) - toFixed(0) here is just a defensive belt-and-suspenders in
    // case that ever changes, not the primary place rounding happens.
    function setPieFill(percent) {
        const wholePercent = Math.round(percent);
        pieRingElement.style.background = `conic-gradient(var(--vscode-progressBar-background, #0e70c0) ${wholePercent}%, var(--vscode-editorWidget-background, #3c3c3c) 0)`;
        percentElement.textContent = wholePercent + '%';
    }

    // Signals the extension host that this document has actually finished
    // loading and run its own script - there is no VS Code-side buffering
    // of postMessage calls sent before that point, so without this
    // handshake any progress the host already knows about (e.g. this
    // document replacing an earlier one mid-parse, though that shouldn't
    // normally happen) would be silently dropped. The host replies with
    // its own most recently computed { percent, label } via a
    // 'nettraceProgress' message - see DotnetInsightsNettraceEditor.ts's
    // own onDidReceiveMessage.
    vscode.postMessage({ type: 'nettraceLoadingReady' });

    window.addEventListener('message', function (event) {
        const message = event.data;

        if (message.type === 'nettraceProgress') {
            // First real update switches the ring from indeterminate
            // (unknown, still-shown-as-moving via a spin animation - see
            // nettraceLoading.css) to determinate - see
            // NettraceLoadingRenderer.ts's own comment on why this starts
            // indeterminate rather than pinned at 0%. Removing the class
            // also stops the spin animation and clears the class's own
            // fixed-wedge background, which setPieFill's inline style
            // then overrides with the real value.
            pieRingElement.classList.remove('nettraceLoadingPieIndeterminate');
            setPieFill(message.percent);
            labelElement.textContent = message.label;
            return;
        }

        if (message.type === 'nettraceError') {
            // Freeze the clock at the point of failure. Nothing is running
            // any more, so a still-ticking timer under a "Failed" label
            // would read as work still in progress.
            stopElapsed();
            pieRingElement.classList.remove('nettraceLoadingPieIndeterminate');
            errorElement.textContent = message.message;
            errorElement.style.display = 'block';
            labelElement.textContent = 'Failed';
            return;
        }
    });
})();
