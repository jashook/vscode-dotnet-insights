////////////////////////////////////////////////////////////////////////////////
// Module: workload.c
//
// Notes:
// Deliberately-shaped native workload used to capture perf.data fixtures for
// perfParser. It is NOT a benchmark - every thread here exists to produce one
// specific, recognizable shape in the capture, so that a view rendering it
// wrongly is obvious rather than merely plausible:
//
//   - hot_leaf_alpha/beta are reached through DISTINCT call chains from the
//     same root, so a caller tree that merges them (or attributes one's
//     samples to the other's chain) is visible immediately.
//   - contender threads all block on one mutex held for a long, fixed span,
//     so lock:contention_* events have a known owner, a known count, and a
//     wait time that should dominate every other lock in the capture.
//   - parked_thread never wakes. A CPU view that ranks it, or a threading
//     view that calls it "blocked" rather than "parked by design", is wrong
//     in the same way the .NET side's benignly-parked classification exists
//     to prevent (see nettraceParser/Threading/ThreadActivityProfiler.cs).
//   - churn_thread alternates short work and short sleeps, so it is neither
//     parked nor pinned - the case that separates a duty-cycle measurement
//     from a binary running/blocked flag.
//
// Built -fno-omit-frame-pointer on purpose: perf's default unwind on both
// x86_64 and aarch64 is frame-pointer based, and an -O2 build without it
// produces one-frame callchains that make every stack-shaped assertion
// vacuous rather than failing.
//
// Every iteration count is read from `workUnits`, a VOLATILE global, and this
// is load-bearing rather than stylistic. The compute_* chain is pure - it
// reads no global and has no side effect - so with a literal iteration count
// GCC correctly recognises the whole call as loop-invariant and hoists it out
// of the worker loop, computing it once. The first capture taken with this
// file was measured doing exactly that: `cpu_thread` reported 34.32% SELF time
// with no children at all, because its loop really had been reduced to
// `sink += <constant>`, while the contender threads - whose loops contain
// opaque pthread_mutex_lock calls that block the hoist - kept their full
// four-frame chain. A volatile read the optimiser must repeat each iteration
// is what keeps the call inside the loop. `noinline` alone does NOT do this:
// it prevents inlining, not hoisting.
////////////////////////////////////////////////////////////////////////////////

#define _GNU_SOURCE
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

static volatile double sink = 0.0;
// See this file's header: volatile so the compute_* calls below cannot be
// hoisted out of their worker loops as loop-invariant.
static volatile int workUnits = 200000;
static pthread_mutex_t hotLock = PTHREAD_MUTEX_INITIALIZER;
static pthread_mutex_t coldLock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t neverSignalled = PTHREAD_COND_INITIALIZER;
static pthread_mutex_t parkLock = PTHREAD_MUTEX_INITIALIZER;
static volatile int running = 1;

__attribute__((noinline)) static double hot_leaf_alpha(int iterations)
{
    double accumulator = 0.0;
    for (int index = 0; index < iterations; ++index)
    {
        accumulator += (double)(index % 7) * 1.000001;
    }

    return accumulator;
}

__attribute__((noinline)) static double hot_leaf_beta(int iterations)
{
    double accumulator = 1.0;
    for (int index = 0; index < iterations; ++index)
    {
        accumulator = accumulator * 1.0000001 + (double)(index & 3);
    }

    return accumulator;
}

__attribute__((noinline)) static double compute_middle_left(int iterations)
{
    return hot_leaf_alpha(iterations) + hot_leaf_beta(iterations / 4);
}

__attribute__((noinline)) static double compute_middle_right(int iterations)
{
    return hot_leaf_beta(iterations);
}

__attribute__((noinline)) static double compute_root(int iterations, int takeLeft)
{
    if (takeLeft)
    {
        return compute_middle_left(iterations);
    }

    return compute_middle_right(iterations);
}

static void sleep_ms(long milliseconds)
{
    struct timespec request;
    request.tv_sec = milliseconds / 1000;
    request.tv_nsec = (milliseconds % 1000) * 1000000L;
    nanosleep(&request, NULL);
}

// Holds hotLock for a long, fixed span so every contender's wait is
// attributable to one identifiable owner.
static void *lock_owner_thread(void *argument)
{
    (void)argument;
    pthread_setname_np(pthread_self(), "lock-owner");
    while (running)
    {
        pthread_mutex_lock(&hotLock);
        sink += compute_root(workUnits * 2, 1);
        pthread_mutex_unlock(&hotLock);
        sleep_ms(2);
    }

    return NULL;
}

static void *lock_contender_thread(void *argument)
{
    long threadIndex = (long)argument;
    char name[16];
    snprintf(name, sizeof(name), "contender-%ld", threadIndex);
    pthread_setname_np(pthread_self(), name);
    while (running)
    {
        pthread_mutex_lock(&hotLock);
        sink += compute_root(workUnits / 100, 0);
        pthread_mutex_unlock(&hotLock);

        pthread_mutex_lock(&coldLock);
        sink += 1.0;
        pthread_mutex_unlock(&coldLock);
    }

    return NULL;
}

// Pure CPU, never touches a lock - the control against which every
// contention number is read.
static void *cpu_thread(void *argument)
{
    long threadIndex = (long)argument;
    char name[16];
    snprintf(name, sizeof(name), "cpu-%ld", threadIndex);
    pthread_setname_np(pthread_self(), name);
    while (running)
    {
        sink += compute_root(workUnits, (int)(threadIndex & 1));
    }

    return NULL;
}

// Parked for the whole capture, by design.
static void *parked_thread(void *argument)
{
    (void)argument;
    pthread_setname_np(pthread_self(), "parked");
    pthread_mutex_lock(&parkLock);
    while (running)
    {
        pthread_cond_wait(&neverSignalled, &parkLock);
    }

    pthread_mutex_unlock(&parkLock);
    return NULL;
}

// Neither parked nor pinned: a real duty cycle.
static void *churn_thread(void *argument)
{
    (void)argument;
    pthread_setname_np(pthread_self(), "churn");
    while (running)
    {
        sink += compute_root(workUnits / 4, 1);
        sleep_ms(10);
    }

    return NULL;
}

int main(int argumentCount, char **argumentValues)
{
    int durationSeconds = 10;
    if (argumentCount > 1)
    {
        durationSeconds = atoi(argumentValues[1]);
    }

    int contenderCount = 4;
    int cpuThreadCount = 2;
    pthread_t threads[16];
    int threadCount = 0;

    pthread_create(&threads[threadCount++], NULL, lock_owner_thread, NULL);
    for (long index = 0; index < contenderCount; ++index)
    {
        pthread_create(&threads[threadCount++], NULL, lock_contender_thread, (void *)index);
    }

    for (long index = 0; index < cpuThreadCount; ++index)
    {
        pthread_create(&threads[threadCount++], NULL, cpu_thread, (void *)index);
    }

    pthread_create(&threads[threadCount++], NULL, parked_thread, NULL);
    pthread_create(&threads[threadCount++], NULL, churn_thread, NULL);

    sleep(durationSeconds);
    running = 0;
    pthread_cond_broadcast(&neverSignalled);

    for (int index = 0; index < threadCount; ++index)
    {
        pthread_join(threads[index], NULL);
    }

    printf("sink=%f\n", sink);
    return 0;
}
