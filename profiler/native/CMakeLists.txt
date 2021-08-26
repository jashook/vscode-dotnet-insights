################################################################################
# Module: CMakeLists.txt
#
################################################################################

cmake_minimum_required(VERSION 3.17)
project (ev31_profiler)

if (WIN32)
    enable_language(ASM_MASM)
else()
    enable_language(C ASM)
endif()

# Find all cpp files
FILE(GLOB cpp_sources *.cpp src/*.cpp)

if (WIN32)
    FILE(GLOB asm_sources asm/amd64/windows/*.asm)
else()
    FILE(GLOB asm_sources asm/amd64/unix/*.S)
endif()


# Create the executable
add_library(ev31_profiler SHARED ${cpp_sources} ${asm_sources})

if (WIN32)
    target_include_directories(ev31_profiler PRIVATE inc ../../../runtime/src/coreclr/src/inc)
else()
    target_include_directories(ev31_profiler PRIVATE inc ../../../runtime/src/coreclr/src/inc ../../../runtime/src/coreclr/src/pal/inc/rt ../../../runtime/src/coreclr/src/pal/inc ../../../runtime/src/coreclr/src/pal/prebuilt/inc)
endif()

add_compile_options(-fexceptions)
add_definitions(-DUSE_STL)

add_definitions(-DPAL_STDCPP_COMPAT)
add_definitions(-DBIT64)
add_definitions(-DPLATFORM_UNIX)

if (!WIN32)

    add_definitions(-fms-extensions)
    add_definitions(-fPIC)
    add_definitions(-Wno-invalid-noreturn)
    add_definitions(-Wno-pragma-pack)
    add_definitions(-Wno-int-to-pointer-cast)
endif()


target_compile_features(ev31_profiler PRIVATE cxx_std_17)