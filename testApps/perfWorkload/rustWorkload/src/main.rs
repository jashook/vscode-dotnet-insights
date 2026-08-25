////////////////////////////////////////////////////////////////////////////////
// Rust workload used to capture perf.data fixtures for perfParser.
//
// It is NOT a benchmark. Every item here exists to put one specific,
// recognizable shape into the capture, chosen so that a view rendering it
// wrongly is obvious rather than merely plausible - and specifically so that
// the Rust-shaped problems a native profiler has to solve are all present:
//
//   - `compute_generic` is monomorphized over two types, so ONE source
//     function becomes two distinct symbols with different addresses. A view
//     that merges them is hiding real information; a view that cannot relate
//     them is unreadable on real Rust code, where this happens hundreds of
//     times.
//   - `iterator_chain` puts closures on the stack, whose symbols carry the
//     `{{closure}}` component - the single most common frame in real Rust
//     profiles and the one whose name is least useful unless its parent is
//     shown with it.
//   - `run_dynamic` dispatches through a trait object, so the call cannot be
//     devirtualized and a real frame survives to be sampled.
//   - `recurse` produces a genuinely deep stack, which is what bounds
//     callchain depth handling.
//   - Every leaf is reached through TWO distinct call paths, so a caller tree
//     that merges paths, or attributes one path's samples to another, shows
//     up immediately.
//
// std::hint::black_box is load-bearing, not decoration. The compute chain is
// pure, so with a constant iteration count LLVM hoists the whole call out of
// the worker loop and computes it once. The C sibling of this file was
// measured doing exactly that before it was fixed: a worker reported 34% SELF
// time with no children, because its loop really had been reduced to
// `sink += <constant>`. black_box is the Rust equivalent of the volatile read
// used there - `#[inline(never)]` does NOT prevent it, since it stops inlining
// rather than hoisting.
////////////////////////////////////////////////////////////////////////////////

use std::hint::black_box;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread;
use std::time::Duration;

////////////////////////////////////////////////////////////////////////////////
// Leaves. Both are reached from more than one path on purpose.
////////////////////////////////////////////////////////////////////////////////

#[inline(never)]
fn hot_leaf_alpha(iterations: u64) -> f64 {
    let mut accumulator = 0.0f64;
    for index in 0..iterations {
        accumulator += (index % 7) as f64 * 1.000_001;
    }

    accumulator
}

#[inline(never)]
fn hot_leaf_beta(iterations: u64) -> f64 {
    let mut accumulator = 1.0f64;
    for index in 0..iterations {
        accumulator = accumulator * 1.000_000_1 + (index & 3) as f64;
    }

    accumulator
}

////////////////////////////////////////////////////////////////////////////////
// One generic function, two monomorphizations, two symbols.
////////////////////////////////////////////////////////////////////////////////

trait Accumulate {
    fn seed() -> f64;
    fn step(accumulator: f64, index: u64) -> f64;
}

struct AlphaKind;
struct BetaKind;

impl Accumulate for AlphaKind {
    fn seed() -> f64 {
        0.0
    }

    fn step(accumulator: f64, index: u64) -> f64 {
        accumulator + (index % 5) as f64
    }
}

impl Accumulate for BetaKind {
    fn seed() -> f64 {
        1.0
    }

    fn step(accumulator: f64, index: u64) -> f64 {
        accumulator * 1.000_000_1 + (index & 1) as f64
    }
}

#[inline(never)]
fn compute_generic<K: Accumulate>(iterations: u64) -> f64 {
    let mut accumulator = K::seed();
    for index in 0..iterations {
        accumulator = K::step(accumulator, index);
    }

    accumulator + hot_leaf_alpha(iterations / 8)
}

////////////////////////////////////////////////////////////////////////////////
// Closures, iterator adapters, and a trait object.
////////////////////////////////////////////////////////////////////////////////

#[inline(never)]
fn iterator_chain(iterations: u64) -> f64 {
    (0..iterations)
        .map(|index| (index % 11) as f64 * 0.5)
        .filter(|value| *value > 0.25)
        .fold(0.0f64, |accumulator, value| accumulator + value)
        + hot_leaf_beta(iterations / 8)
}

trait Workload: Send + Sync {
    fn run(&self, iterations: u64) -> f64;
    fn name(&self) -> &'static str;
}

struct AlphaWorkload;
struct BetaWorkload;

impl Workload for AlphaWorkload {
    fn run(&self, iterations: u64) -> f64 {
        hot_leaf_alpha(iterations)
    }

    fn name(&self) -> &'static str {
        "alpha"
    }
}

impl Workload for BetaWorkload {
    fn run(&self, iterations: u64) -> f64 {
        hot_leaf_beta(iterations)
    }

    fn name(&self) -> &'static str {
        "beta"
    }
}

#[inline(never)]
fn run_dynamic(workload: &dyn Workload, iterations: u64) -> f64 {
    workload.run(iterations)
}

////////////////////////////////////////////////////////////////////////////////
// A genuinely deep stack.
////////////////////////////////////////////////////////////////////////////////

#[inline(never)]
fn recurse(depth: u32, iterations: u64) -> f64 {
    if depth == 0 {
        return hot_leaf_beta(iterations);
    }

    black_box(recurse(depth - 1, iterations))
}

////////////////////////////////////////////////////////////////////////////////
// Threads. Each one is a distinct, named shape.
////////////////////////////////////////////////////////////////////////////////

struct Shared {
    running: AtomicBool,
    hot_lock: Mutex<f64>,
    cold_lock: Mutex<f64>,
    park_lock: Mutex<bool>,
    never_signalled: Condvar,
    work_units: u64,
}

// Holds hot_lock across a long compute, so every contender's wait is
// attributable to one identifiable owner.
fn lock_owner_thread(shared: Arc<Shared>) {
    while shared.running.load(Ordering::Relaxed) {
        {
            let mut guard = shared.hot_lock.lock().unwrap();
            *guard += compute_generic::<AlphaKind>(black_box(shared.work_units) * 2);
        }

        thread::sleep(Duration::from_millis(2));
    }
}

fn lock_contender_thread(shared: Arc<Shared>) {
    while shared.running.load(Ordering::Relaxed) {
        {
            let mut guard = shared.hot_lock.lock().unwrap();
            *guard += iterator_chain(black_box(shared.work_units) / 100);
        }

        {
            let mut guard = shared.cold_lock.lock().unwrap();
            *guard += 1.0;
        }
    }
}

// Pure CPU, never touches a lock - the control every contention number is read
// against.
fn cpu_thread(shared: Arc<Shared>, take_alpha: bool) {
    let mut sink = 0.0f64;
    while shared.running.load(Ordering::Relaxed) {
        let iterations = black_box(shared.work_units);
        sink += if take_alpha {
            compute_generic::<AlphaKind>(iterations)
        } else {
            compute_generic::<BetaKind>(iterations)
        };
    }

    black_box(sink);
}

fn dynamic_thread(shared: Arc<Shared>) {
    let workloads: Vec<Box<dyn Workload>> = vec![Box::new(AlphaWorkload), Box::new(BetaWorkload)];
    let mut sink = 0.0f64;
    let mut turn = 0usize;

    while shared.running.load(Ordering::Relaxed) {
        let workload = &workloads[turn % workloads.len()];
        sink += run_dynamic(workload.as_ref(), black_box(shared.work_units) / 2);
        turn += 1;
    }

    black_box(sink);
}

fn deep_stack_thread(shared: Arc<Shared>) {
    let mut sink = 0.0f64;
    while shared.running.load(Ordering::Relaxed) {
        sink += recurse(24, black_box(shared.work_units) / 32);
    }

    black_box(sink);
}

// Parked for the whole capture, by design. Produces ZERO cpu-clock samples -
// which is the point: a CPU view that ranks it, or a threading view that calls
// it stuck rather than parked, is wrong.
fn parked_thread(shared: Arc<Shared>) {
    let mut parked = shared.park_lock.lock().unwrap();
    while shared.running.load(Ordering::Relaxed) {
        let result = shared
            .never_signalled
            .wait_timeout(parked, Duration::from_millis(500))
            .unwrap();
        parked = result.0;
    }
}

// Neither parked nor pinned: a real duty cycle.
fn churn_thread(shared: Arc<Shared>) {
    let mut sink = 0.0f64;
    while shared.running.load(Ordering::Relaxed) {
        sink += iterator_chain(black_box(shared.work_units) / 4);
        thread::sleep(Duration::from_millis(10));
    }

    black_box(sink);
}

////////////////////////////////////////////////////////////////////////////////

fn main() {
    let duration_seconds: u64 = std::env::args()
        .nth(1)
        .and_then(|value| value.parse().ok())
        .unwrap_or(10);

    let shared = Arc::new(Shared {
        running: AtomicBool::new(true),
        hot_lock: Mutex::new(0.0),
        cold_lock: Mutex::new(0.0),
        park_lock: Mutex::new(false),
        never_signalled: Condvar::new(),
        work_units: 200_000,
    });

    let mut handles = Vec::new();

    let named: Vec<(&str, Box<dyn FnOnce(Arc<Shared>) + Send>)> = vec![
        ("lock-owner", Box::new(lock_owner_thread)),
        ("contender-0", Box::new(lock_contender_thread)),
        ("contender-1", Box::new(lock_contender_thread)),
        ("contender-2", Box::new(lock_contender_thread)),
        ("contender-3", Box::new(lock_contender_thread)),
        ("cpu-alpha", Box::new(|shared| cpu_thread(shared, true))),
        ("cpu-beta", Box::new(|shared| cpu_thread(shared, false))),
        ("dynamic", Box::new(dynamic_thread)),
        ("deep-stack", Box::new(deep_stack_thread)),
        ("parked", Box::new(parked_thread)),
        ("churn", Box::new(churn_thread)),
    ];

    for (name, body) in named {
        let shared_for_thread = Arc::clone(&shared);
        handles.push(
            thread::Builder::new()
                .name(name.to_string())
                .spawn(move || body(shared_for_thread))
                .unwrap(),
        );
    }

    thread::sleep(Duration::from_secs(duration_seconds));
    shared.running.store(false, Ordering::Relaxed);
    shared.never_signalled.notify_all();

    for handle in handles {
        let _ = handle.join();
    }

    println!("hot={} cold={}", shared.hot_lock.lock().unwrap(), shared.cold_lock.lock().unwrap());
}
