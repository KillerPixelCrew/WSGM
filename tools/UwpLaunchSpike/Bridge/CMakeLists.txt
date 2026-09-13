cmake_minimum_required(VERSION 3.20)
project(WsgmUwpBridge LANGUAGES C CXX)

# Reuse the MinHook source already restored for SteamInput's minhook-sys crate.
# This spike has no dependency on SteamInput's private build outputs.
if(NOT EXISTS "${MINHOOK_SOURCE}/include/MinHook.h")
  message(FATAL_ERROR "Set MINHOOK_SOURCE to minhook-sys-0.1.1/minhook")
endif()
add_library(WsgmUwpBridge SHARED Bridge.cpp InputActivation.cpp
  "${MINHOOK_SOURCE}/src/buffer.c"
  "${MINHOOK_SOURCE}/src/hook.c"
  "${MINHOOK_SOURCE}/src/trampoline.c"
  "${MINHOOK_SOURCE}/src/hde/hde64.c")
target_include_directories(WsgmUwpBridge PRIVATE "${MINHOOK_SOURCE}/include")
target_compile_features(WsgmUwpBridge PRIVATE cxx_std_17)
target_compile_definitions(WsgmUwpBridge PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(WsgmUwpBridge PRIVATE runtimeobject)
set_property(TARGET WsgmUwpBridge PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(WsgmUwpInputProbe SHARED InputProbe.cpp
  "${MINHOOK_SOURCE}/src/buffer.c"
  "${MINHOOK_SOURCE}/src/hook.c"
  "${MINHOOK_SOURCE}/src/trampoline.c"
  "${MINHOOK_SOURCE}/src/hde/hde64.c")
target_include_directories(WsgmUwpInputProbe PRIVATE "${MINHOOK_SOURCE}/include")
target_compile_features(WsgmUwpInputProbe PRIVATE cxx_std_17)
target_compile_definitions(WsgmUwpInputProbe PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(WsgmUwpInputProbe PRIVATE runtimeobject)
set_property(TARGET WsgmUwpInputProbe PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
