################################################################################
# Module: CMakeLists.txt
#
################################################################################

cmake_minimum_required(VERSION 3.21)
project (ev31_profiler)

if (WIN32)
    enable_language(ASM_MASM)
else()
    enable_language(C ASM)
endif()

FILE(GLOB cpp_sources *.cpp src/*.cpp)

if (WIN32)
    FILE(GLOB asm_sources asm/amd64/windows/*.asm)
    set(def_sources ${PROJECT_SOURCE_DIR}/ev31_profiler.def)
else()
    FILE(GLOB asm_sources asm/amd64/unix/*.S)
endif()

# Create the executable
add_library(ev31_profiler SHARED ${cpp_sources} ${asm_sources} ${def_sources})

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

if(WIN32)
    add_definitions(-DWIN32_LEAN_AND_MEAN)
    target_link_libraries(ev31_profiler wsock32 ws2_32)
    target_link_libraries(ev31_profiler httpapi)
endif()

target_compile_features(ev31_profiler PRIVATE cxx_std_17)