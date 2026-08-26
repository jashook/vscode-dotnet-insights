// Draws the Contention view's Overview tab charts.
//
// Every number rendered here comes straight out of the "overview" block
// nettraceParser/Contention/ContentionOverviewBuilder.cs emits - this file
// formats and plots, it does not derive metrics. The two exceptions are
// explicitly labelled divisions against values that are themselves on screen
// (blocked wall clock as a share of its own bucket, and blocked thread-time
// as an average thread count), so a reader can check them by hand.
//
// Chart.js 2.x, like every other chart in this webview: scales.yAxes is an
// ARRAY of axis objects with `id`, not a 3.x `scales.y` object, and tooltips
// live at options.tooltips rather than options.plugins.tooltip. See CLAUDE.md.
//
// All three charts share one x-axis grid (the builder guarantees it), so the
// bucket at position i means the same instant in each of them. That is the
// whole point of the tab: a reader lines up a stall in one chart against a
// spike in another by eye, at the same x.

(function () {
    var blockedChartHandle = null;
    var percentileChartHandle = null;
    var cpuChartHandle = null;

    // Retained so the log/linear toggle can redraw the percentile chart
    // without the caller having to hand the data back in.
    var currentOverview = null;

    function formatElapsedForOverviewChart(totalMSec) {
        var totalSeconds = totalMSec / 1000;
        var minutes = Math.floor(totalSeconds / 60);
        var seconds = totalSeconds - (minutes * 60);

        if (minutes > 0) {
            return minutes + "m " + seconds.toFixed(1) + "s";
        }

        return seconds.toFixed(2) + "s";
    }

    function formatMSecForOverviewChart(value) {
        if (typeof value !== 'number' || !isFinite(value)) {
            return "n/a";
        }

        if (value === 0) {
            return "0 ms";
        }

        if (value < 1) {
            return value.toFixed(3) + " ms";
        }

        if (value < 1000) {
            return value.toFixed(2) + " ms";
        }

        return (value / 1000).toFixed(2) + " s";
    }

    function buildBucketLabels(overview) {
        var labels = [];
        for (var bucketIndex = 0; bucketIndex < overview["bucketCount"]; ++bucketIndex) {
            labels.push(formatElapsedForOverviewChart(overview["minRelativeMSec"] + (bucketIndex * overview["bucketDurationMSec"])));
        }

        return labels;
    }

    function destroyChart(handle) {
        if (handle) {
            handle.destroy();
        }

        return null;
    }

    // Shared axis/tooltip options. maintainAspectRatio is off because the
    // container sizes the canvas, and animation is disabled to match every
    // other chart on this page (a 100-point redraw on a tab switch should not
    // animate).
    function baseChartOptions(yAxes, tooltipLabelCallback) {
        return {
            animation: { duration: 0 },
            responsive: true,
            maintainAspectRatio: false,
            legend: { display: true, position: 'bottom' },
            hover: { mode: 'index', intersect: false },
            scales: {
                xAxes: [{ display: false }],
                yAxes: yAxes
            },
            tooltips: {
                mode: 'index',
                intersect: false,
                callbacks: {
                    title: function (tooltipItems) {
                        return tooltipItems.length > 0 ? "at " + tooltipItems[0].xLabel : '';
                    },
                    label: tooltipLabelCallback
                }
            }
        };
    }

    // Chart 1 - "Time Blocked Over Time". The direct answer to "how much of
    // the clock went to lock waits, when": a shaded area of blocked wall clock
    // (a union across threads, so it can never exceed the bucket) with thread
    // concurrency on its own right-hand axis, because the two answer different
    // questions and share no unit.
    function renderBlockedChart(overview) {
        var canvas = document.getElementById('contentionOverviewBlockedChart');
        if (!canvas || typeof Chart === 'undefined') {
            return;
        }

        blockedChartHandle = destroyChart(blockedChartHandle);

        var bucketDurationMSec = overview["bucketDurationMSec"];
        var blockedWallClock = overview["blockedWallClockMSecByBucket"] || [];
        var blockedThreadMSec = overview["blockedThreadMSecByBucket"] || [];
        var peakThreads = overview["peakThreadsBlockedByBucket"] || [];

        var averageThreadsByBucket = [];
        for (var bucketIndex = 0; bucketIndex < overview["bucketCount"]; ++bucketIndex) {
            // Summed blocked thread-time over the bucket's own duration. The
            // division is stated in the axis label rather than hidden, and
            // both operands are on the chart.
            averageThreadsByBucket.push(bucketDurationMSec > 0 ? (blockedThreadMSec[bucketIndex] || 0) / bucketDurationMSec : 0);
        }

        blockedChartHandle = new Chart(canvas, {
            type: 'line',
            data: {
                labels: buildBucketLabels(overview),
                datasets: [
                    {
                        label: 'Blocked wall clock (ms in bucket)',
                        data: blockedWallClock,
                        yAxisID: 'yBlocked',
                        borderColor: 'rgba(180, 80, 80, 0.9)',
                        backgroundColor: 'rgba(180, 80, 80, 0.25)',
                        borderWidth: 1,
                        pointRadius: 2,
                        fill: true
                    },
                    {
                        label: 'Avg threads blocked',
                        data: averageThreadsByBucket,
                        yAxisID: 'yThreads',
                        borderColor: 'rgba(70, 130, 190, 0.9)',
                        backgroundColor: 'rgba(70, 130, 190, 0.1)',
                        borderWidth: 1,
                        pointRadius: 2,
                        fill: false
                    },
                    {
                        label: 'Peak threads blocked',
                        data: peakThreads,
                        yAxisID: 'yThreads',
                        borderColor: 'rgba(120, 120, 190, 0.7)',
                        borderDash: [4, 3],
                        borderWidth: 1,
                        pointRadius: 0,
                        fill: false
                    }
                ]
            },
            options: baseChartOptions(
                [
                    {
                        id: 'yBlocked',
                        position: 'left',
                        display: true,
                        scaleLabel: { display: true, labelString: 'Blocked wall clock (ms)' },
                        ticks: { beginAtZero: true, max: bucketDurationMSec }
                    },
                    {
                        id: 'yThreads',
                        position: 'right',
                        display: true,
                        scaleLabel: { display: true, labelString: 'Threads blocked' },
                        gridLines: { drawOnChartArea: false },
                        ticks: { beginAtZero: true }
                    }
                ],
                function (tooltipItem, data) {
                    var datasetLabel = data.datasets[tooltipItem.datasetIndex].label;

                    if (tooltipItem.datasetIndex === 0) {
                        var sharePercent = bucketDurationMSec > 0 ? (tooltipItem.yLabel * 100 / bucketDurationMSec) : 0;
                        return datasetLabel + ": " + formatMSecForOverviewChart(tooltipItem.yLabel) + " (" + sharePercent.toFixed(1) + "% of bucket)";
                    }

                    return datasetLabel + ": " + Number(tooltipItem.yLabel).toFixed(2);
                })
        });
    }

    // Chart 2 - percentiles of the waits that STARTED in each bucket.
    //
    // Empty buckets are null rather than 0 (with spanGaps on) for two reasons:
    // a zero would draw a line to the floor and read as "waits got fast here"
    // when nothing was measured at all, and a logarithmic axis cannot plot a
    // zero in the first place. Log is the default because p50 and p99
    // routinely differ by three orders of magnitude on a real capture, which
    // on a linear axis flattens the median into the x-axis.
    function renderPercentileChart(overview, useLinearScale) {
        var canvas = document.getElementById('contentionOverviewPercentileChart');
        if (!canvas || typeof Chart === 'undefined') {
            return;
        }

        percentileChartHandle = destroyChart(percentileChartHandle);

        var contentionCountByBucket = overview["contentionCountByBucket"] || [];

        function seriesFor(sourceArray) {
            var series = [];
            for (var bucketIndex = 0; bucketIndex < overview["bucketCount"]; ++bucketIndex) {
                var hadContention = (contentionCountByBucket[bucketIndex] || 0) > 0;
                var value = sourceArray ? sourceArray[bucketIndex] : 0;

                // A real measured 0 also cannot go on a log axis; it becomes a
                // gap there and stays a 0 on the linear scale.
                if (!hadContention || (!useLinearScale && !(value > 0))) {
                    series.push(null);
                } else {
                    series.push(value);
                }
            }

            return series;
        }

        percentileChartHandle = new Chart(canvas, {
            type: 'line',
            data: {
                labels: buildBucketLabels(overview),
                datasets: [
                    {
                        label: 'Max wait (ms)',
                        data: seriesFor(overview["maxWaitMSecByBucket"]),
                        borderColor: 'rgba(200, 90, 60, 0.85)',
                        backgroundColor: 'rgba(200, 90, 60, 0.12)',
                        borderWidth: 1,
                        pointRadius: 1,
                        spanGaps: true,
                        fill: false
                    },
                    {
                        label: 'p99 wait (ms)',
                        data: seriesFor(overview["p99WaitMSecByBucket"]),
                        borderColor: 'rgba(210, 150, 60, 0.95)',
                        borderWidth: 2,
                        pointRadius: 1,
                        spanGaps: true,
                        fill: false
                    },
                    {
                        label: 'p50 wait (ms)',
                        data: seriesFor(overview["p50WaitMSecByBucket"]),
                        borderColor: 'rgba(90, 160, 110, 0.95)',
                        borderWidth: 2,
                        pointRadius: 1,
                        spanGaps: true,
                        fill: false
                    }
                ]
            },
            options: baseChartOptions(
                [{
                    id: 'yWait',
                    position: 'left',
                    display: true,
                    type: useLinearScale ? 'linear' : 'logarithmic',
                    scaleLabel: { display: true, labelString: useLinearScale ? 'Wait (ms)' : 'Wait (ms, log)' },
                    ticks: {
                        beginAtZero: useLinearScale,
                        // Chart.js 2.x's logarithmic axis labels every minor
                        // tick by default, which on a four-decade range is an
                        // unreadable stack of numbers.
                        callback: function (tickValue) {
                            if (useLinearScale) {
                                return tickValue;
                            }

                            var exponent = Math.log10(tickValue);
                            return Math.abs(exponent - Math.round(exponent)) < 1e-9 ? formatMSecForOverviewChart(tickValue) : '';
                        }
                    }
                }],
                function (tooltipItem, data) {
                    return data.datasets[tooltipItem.datasetIndex].label + ": " + formatMSecForOverviewChart(tooltipItem.yLabel);
                })
        });
    }

    // Chart 3 - the correlation chart this tab was built for.
    //
    // Blocked thread-time and CPU-bound samples have no common unit, so they
    // get their own axes; the question is whether their SHAPES line up, not
    // their magnitudes. GC pause shares the left axis in milliseconds - it is
    // the competing explanation for any CPU spike here, and having to open
    // another view to rule it out is how a wrong conclusion gets drawn.
    function selectedCpuSeries(overview) {
        var cpuSeries = overview["cpuSeries"] || [];
        if (cpuSeries.length === 0) {
            return null;
        }

        var select = document.getElementById('contentionOverviewCpuSeriesSelect');
        var wantedId = select ? select.value : null;

        for (var seriesIndex = 0; seriesIndex < cpuSeries.length; ++seriesIndex) {
            if (cpuSeries[seriesIndex]["id"] === wantedId) {
                return cpuSeries[seriesIndex];
            }
        }

        return cpuSeries[0];
    }

    function renderCpuChart(overview) {
        var canvas = document.getElementById('contentionOverviewCpuChart');
        if (!canvas || typeof Chart === 'undefined' || !overview["hasCpuSamples"]) {
            return;
        }

        cpuChartHandle = destroyChart(cpuChartHandle);

        var series = selectedCpuSeries(overview);
        if (!series) {
            return;
        }

        // The definition and its known bias travel WITH the plotted line - a
        // reader who switches the selector and sees the shape change needs to
        // know which way the new definition is wrong without scrolling to the
        // table below.
        var hint = document.getElementById('contentionOverviewCpuSeriesHint');
        if (hint) {
            hint.textContent = series["definition"] + " \u00b7 " + series["bias"];
        }

        // When the capture is CPU-time sampled, the series is converted from a
        // sample COUNT to CORES BUSY - samples x period gives milliseconds of
        // CPU in the bucket, and dividing by the bucket's own wall-clock
        // duration gives how many cores were busy on average across it. That
        // is the number the question "do lock stalls cause CPU spikes" is
        // actually about, and it is comparable across captures and machines in
        // a way a sample count never is.
        var hasCpuTime = overview["hasCpuTime"] === true;
        var samplePeriodMSec = overview["samplePeriodMSec"] || 0;
        var bucketDurationMSec = overview["bucketDurationMSec"] || 0;
        var cpuSeriesData = series["samplesByBucket"] || [];
        var cpuAxisLabel = series["label"] + ' (samples)';

        if (hasCpuTime && samplePeriodMSec > 0 && bucketDurationMSec > 0) {
            var coresByBucket = [];
            for (var cpuBucketIndex = 0; cpuBucketIndex < cpuSeriesData.length; ++cpuBucketIndex) {
                coresByBucket.push((cpuSeriesData[cpuBucketIndex] * samplePeriodMSec) / bucketDurationMSec);
            }

            cpuSeriesData = coresByBucket;
            cpuAxisLabel = 'CPU cores busy (' + series["label"].toLowerCase() + ')';
        }

        var datasets = [
            {
                label: 'Blocked thread-time (ms)',
                data: overview["blockedThreadMSecByBucket"] || [],
                yAxisID: 'yBlockedThreadTime',
                borderColor: 'rgba(180, 80, 80, 0.9)',
                backgroundColor: 'rgba(180, 80, 80, 0.22)',
                borderWidth: 1,
                pointRadius: 1,
                fill: true
            },
            {
                label: cpuAxisLabel,
                data: cpuSeriesData,
                yAxisID: 'yCpu',
                borderColor: 'rgba(60, 140, 90, 0.95)',
                borderWidth: 2,
                pointRadius: 1,
                fill: false
            }
        ];

        // GC pause gets its OWN axis rather than sharing the blocked-thread-time
        // one. Measured on a real capture: GC peaks at 239ms in a bucket -
        // 13.7% of that bucket's wall clock, and plainly worth seeing - while
        // blocked thread-time peaks at 124,372ms in the same units, 520x
        // larger. Sharing an axis drew GC as a flat line along zero, which
        // reads as "no GC happened here" rather than "different scale".
        if (overview["hasGcPauses"]) {
            datasets.push({
                label: 'GC pause (ms in bucket)',
                data: overview["gcPauseMSecByBucket"] || [],
                yAxisID: 'yGcPause',
                borderColor: 'rgba(140, 110, 190, 0.9)',
                backgroundColor: 'rgba(140, 110, 190, 0.15)',
                borderDash: [5, 3],
                borderWidth: 1,
                pointRadius: 0,
                fill: true
            });
        }

        cpuChartHandle = new Chart(canvas, {
            type: 'line',
            data: {
                labels: buildBucketLabels(overview),
                datasets: datasets
            },
            options: baseChartOptions(
                [
                    {
                        id: 'yBlockedThreadTime',
                        position: 'left',
                        display: true,
                        scaleLabel: { display: true, labelString: 'Blocked thread-time (ms)' },
                        ticks: { beginAtZero: true }
                    },
                    {
                        id: 'yCpu',
                        position: 'right',
                        display: true,
                        scaleLabel: { display: true, labelString: cpuAxisLabel },
                        gridLines: { drawOnChartArea: false },
                        ticks: { beginAtZero: true }
                    },
                    {
                        id: 'yGcPause',
                        position: 'right',
                        display: overview["hasGcPauses"] === true,
                        scaleLabel: { display: true, labelString: 'GC pause (ms)' },
                        gridLines: { drawOnChartArea: false },
                        // Capped at the bucket's own duration so the height of
                        // the GC band reads directly as "share of this bucket
                        // spent stopped", the same ceiling the Time Blocked
                        // chart uses.
                        ticks: { beginAtZero: true, suggestedMax: overview["bucketDurationMSec"] }
                    }
                ],
                function (tooltipItem, data) {
                    var datasetLabel = data.datasets[tooltipItem.datasetIndex].label;

                    if (data.datasets[tooltipItem.datasetIndex].yAxisID === 'yCpu') {
                        return hasCpuTime
                            ? datasetLabel + ": " + Number(tooltipItem.yLabel).toFixed(2) + " cores"
                            : datasetLabel + ": " + Number(tooltipItem.yLabel).toLocaleString();
                    }

                    if (data.datasets[tooltipItem.datasetIndex].yAxisID === 'yGcPause') {
                        var bucketDurationMSec = overview["bucketDurationMSec"];
                        var gcSharePercent = bucketDurationMSec > 0 ? (tooltipItem.yLabel * 100 / bucketDurationMSec) : 0;
                        return datasetLabel + ": " + formatMSecForOverviewChart(tooltipItem.yLabel) + " (" + gcSharePercent.toFixed(1) + "% of bucket)";
                    }

                    return datasetLabel + ": " + formatMSecForOverviewChart(tooltipItem.yLabel);
                })
        });
    }

    // Called on first reveal of the Overview tab and on every later switch
    // back to it. Idempotent - each chart is destroyed and rebuilt, which is
    // also what makes it correct after the panel was hidden and re-shown at a
    // different width (a canvas sized inside a display:none panel has a
    // zero-width backing store).
    function renderContentionOverviewCharts(overview) {
        if (!overview) {
            return;
        }

        currentOverview = overview;

        var linearScaleToggle = document.getElementById('contentionOverviewLinearScale');
        var useLinearScale = !!(linearScaleToggle && linearScaleToggle.checked);

        renderBlockedChart(overview);
        renderPercentileChart(overview, useLinearScale);
        renderCpuChart(overview);
    }

    function wireContentionOverviewControls() {
        var cpuSeriesSelect = document.getElementById('contentionOverviewCpuSeriesSelect');

        if (cpuSeriesSelect && !cpuSeriesSelect.getAttribute('data-wired')) {
            cpuSeriesSelect.setAttribute('data-wired', 'true');
            cpuSeriesSelect.addEventListener('change', function () {
                if (currentOverview) {
                    renderCpuChart(currentOverview);
                }
            });
        }

        var linearScaleToggle = document.getElementById('contentionOverviewLinearScale');

        if (linearScaleToggle && !linearScaleToggle.getAttribute('data-wired')) {
            linearScaleToggle.setAttribute('data-wired', 'true');
            linearScaleToggle.addEventListener('change', function () {
                if (currentOverview) {
                    renderPercentileChart(currentOverview, linearScaleToggle.checked);
                }
            });
        }
    }

    // Plain globals, matching rankedTable.js/lockTimeline.js - these files are
    // loaded as ordinary scripts before snapshotGcStats.js, not modules.
    window.renderContentionOverviewCharts = renderContentionOverviewCharts;
    window.wireContentionOverviewControls = wireContentionOverviewControls;
})();
