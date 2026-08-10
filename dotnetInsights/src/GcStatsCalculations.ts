// Pure GC-stats math extracted from GcSnapshotRenderer.ts so it can be unit
// tested without any vscode dependency. Operates directly on the
// gcData["gcData"] array (each entry shaped like { data: { TotalHeapSize,
// Heaps: [{ Generations: { 0: {...}, 1: {...}, 2: {...}, 3: {...} } }],
// generation, PauseDurationMSec, ... } }).
//
// NOTE: both functions below call .sort() with no comparator, matching the
// exact pre-existing behavior of the code this was extracted from. Array.sort()
// with no comparator sorts by string comparison, not numeric value - so the
// "median" returned here is the middle element of a *lexicographically*
// sorted array (e.g. [100, 9, 20] sorts to [100, 20, 9], not [9, 20, 100]).
// This is very likely a pre-existing bug, not intentional behavior. It is
// preserved as-is here (not fixed) since extracting this code for testing
// should not silently change what it computes - flagged for a follow-up
// decision instead.

export type StatsSummary = [total: number, mean: number, median: number, max: number, min: number];

export function computeAllocationAmountStats(gcs: any[], generationToUse?: number): [number[], StatsSummary] {
    const kb = 1024;
    let totalAllocations = 0;
    const allocationsBetweenGc: number[] = [];

    for (let index = 0; index < gcs.length; ++index) {
        const currentGc = gcs[index]["data"];
        let newAllocAmount = 0;

        if (generationToUse == undefined) {
            for (let heapIndex = 0; heapIndex < currentGc["Heaps"].length; ++heapIndex) {
                newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][0]["NewAllocation"] / kb;
                newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][1]["NewAllocation"] / kb;
                newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][2]["NewAllocation"] / kb;
                newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][3]["NewAllocation"] / kb;
            }
        }
        else {
            for (let heapIndex = 0; heapIndex < currentGc["Heaps"].length; ++heapIndex) {
                newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][generationToUse]["NewAllocation"] / kb;
            }
        }

        totalAllocations += newAllocAmount;
        allocationsBetweenGc.push(newAllocAmount);
    }

    if (allocationsBetweenGc.length == 0) {
        return [[], [0, 0, 0, 0, 0]];
    }

    let maxAllocationAmountBetweenGcs = 0;
    let lowestAllocationAmountBetweenGcs = allocationsBetweenGc[0];

    for (let index = 0; index < allocationsBetweenGc.length; ++index) {
        if (allocationsBetweenGc[index] > maxAllocationAmountBetweenGcs) {
            maxAllocationAmountBetweenGcs = allocationsBetweenGc[index];
        }
        if (allocationsBetweenGc[index] < lowestAllocationAmountBetweenGcs) {
            lowestAllocationAmountBetweenGcs = allocationsBetweenGc[index];
        }
    }

    allocationsBetweenGc.sort();
    const half = Math.floor(allocationsBetweenGc.length / 2);
    const medianAllocationsBetweenGcs = allocationsBetweenGc[half];
    const meanAllocationBetweenGcs = totalAllocations / allocationsBetweenGc.length;

    return [allocationsBetweenGc, [totalAllocations, meanAllocationBetweenGcs, medianAllocationsBetweenGcs, maxAllocationAmountBetweenGcs, lowestAllocationAmountBetweenGcs]];
}

export function computePauseTimeStats(gcs: any[], generation?: number): [number[], StatsSummary] {
    let totalTimeInGc = 0.0;
    let timesInEachGc: number[] = [];
    let highestTimeInGc = 0;
    let lowestTimeInGc = 0;

    for (let index = 0; index < gcs.length; ++index) {
        if (generation != undefined) {
            if (gcs[index]["data"]["generation"] == generation) {
                timesInEachGc.push(parseFloat(gcs[index]["data"]["PauseDurationMSec"]));
            }
        }
        else {
            timesInEachGc.push(parseFloat(gcs[index]["data"]["PauseDurationMSec"]));
        }
    }

    if (timesInEachGc.length == 0) {
        return [[], [0, 0, 0, 0, 0]];
    }

    lowestTimeInGc = timesInEachGc[0];
    for (let index = 0; index < timesInEachGc.length; ++index) {
        totalTimeInGc += timesInEachGc[index];

        if (timesInEachGc[index] < lowestTimeInGc) {
            lowestTimeInGc = timesInEachGc[index];
        }

        if (timesInEachGc[index] > highestTimeInGc) {
            highestTimeInGc = timesInEachGc[index];
        }
    }

    timesInEachGc.sort();
    const half = Math.floor(timesInEachGc.length / 2);
    const medianTimeInGc = timesInEachGc[half];
    const averageTimeInGc = totalTimeInGc / timesInEachGc.length;

    return [timesInEachGc, [totalTimeInGc, averageTimeInGc, medianTimeInGc, highestTimeInGc, lowestTimeInGc]];
}
