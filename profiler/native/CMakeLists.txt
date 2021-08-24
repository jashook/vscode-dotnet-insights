################################################################################
# Module: CMakeLists.txt
#
################################################################################

cmake_minimum_required(VERSION 3.16)
project (ev31_profiler)

enable_language(C ASM)

# Find all cpp files
FILE(GLOB cpp_sources *.cpp src/*.cpp)
FILE(GLOB asm_sources asm/amd64/unix/*.S)

# Create the executable
add_library(ev31_profiler SHARED ${cpp_sources} ${asm_sources})

target_include_directories(ev31_profiler PRIVATE inc ../../../runtime/src/coreclr/src/inc ../../../runtime/src/coreclr/src/pal/inc/rt ../../../runtime/src/coreclr/src/pal/inc ../../../runtime/src/coreclr/src/pal/prebuilt/inc /Users/jashoo/projects/runtime/artifacts/bin/coreclr/OSX.x64.Checked/inc)

add_compile_options(-fexceptions)
add_definitions(-DUSE_STL)

add_definitions(-DPAL_STDCPP_COMPAT)
add_definitions(-DBIT64)
add_definitions(-DPLATFORM_UNIX)
add_definitions(-fms-extensions)
add_definitions(-fPIC)
add_definitions(-Wno-invalid-noreturn)
add_definitions(-Wno-pragma-pack)
add_definitions(-Wno-int-to-pointer-cast)

target_compile_features(ev31_profiler PRIVATE cxx_std_17)