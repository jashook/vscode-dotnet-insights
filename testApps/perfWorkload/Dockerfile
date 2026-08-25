# Capture rig for perfParser fixtures. Built and run by capture.sh; see that
# script for why the container needs --privileged.
#
# Multi-stage: the Rust workload is built by the official toolchain image and
# copied into the perf image, so the perf image does not carry a Rust
# toolchain and the workload binary is the only thing that crosses.

FROM rust:slim AS rustbuild
WORKDIR /src
COPY rustWorkload/Cargo.toml /src/Cargo.toml
COPY rustWorkload/src /src/src
# force-frame-pointers is REQUIRED, not a tuning knob. Rust's release profile
# omits frame pointers, and `perf record --call-graph fp` then produces
# one-frame callchains for every Rust frame - the profile looks like a flat
# list of leaves with no callers at all. The alternative is
# `--call-graph dwarf`, which shifts the unwind to the analysis tool and needs
# a full DWARF CFI unwinder to read; see perfParser's notes on that fork.
RUN RUSTFLAGS="-C force-frame-pointers=yes" cargo build --release \
 && cp /src/target/release/rust_workload /rust_workload

# rustfilt wraps rustc's own `rustc-demangle`, which is the reference
# implementation of both mangling schemes. It is carried into the perf image
# purely so capture.sh can emit a mangled/demangled ground-truth table for
# every symbol in the fixture binary - perfParser's demangler is diffed against
# that rather than against its author's reading of the RFC.
RUN cargo install rustfilt --root /rustfilt

# A SECOND build of the same workload with rustc's stock release settings -
# no forced frame pointers. This is what anyone actually ships, and it is the
# capture `--call-graph fp` cannot unwind: without frame pointers every
# callchain is one frame deep. It exists so the DWARF unwinder is developed
# against the case it is for, rather than against a build that did not need it.
RUN cargo build --release --target-dir /stock-target \
 && cp /stock-target/release/rust_workload /rust_workload_stock

FROM ubuntu:24.04

RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      build-essential \
      linux-tools-generic \
      elfutils \
      binutils \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /work
COPY workload.c /work/workload.c

# -fno-omit-frame-pointer for the same reason the Rust build forces them.
# -g keeps DWARF so `perf script` can act as ground truth.
RUN gcc -O1 -g -fno-omit-frame-pointer -pthread -o /work/workload /work/workload.c

COPY --from=rustbuild /rust_workload /work/rust_workload
COPY --from=rustbuild /rust_workload_stock /work/rust_workload_stock
COPY --from=rustbuild /rustfilt/bin/rustfilt /usr/local/bin/rustfilt

COPY capture.sh /work/capture.sh
RUN chmod +x /work/capture.sh
ENTRYPOINT ["/work/capture.sh"]
