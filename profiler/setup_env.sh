dir="$(cd -P -- "$(dirname -- "$0")" && pwd -P)"

export CORECLR_PROFILER_PATH=$dir/native/build/libev31_profiler.dylib
export CORECLR_PROFILER={cf0d821e-299b-5307-a3d8-b283c03916dd}
export CORECLR_ENABLE_PROFILING=1