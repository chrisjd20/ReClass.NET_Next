cmake_minimum_required(VERSION 3.16)
project(ReClassNativeCore LANGUAGES C CXX)

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "The container build supports x64 only")
endif()
if(NOT CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64|amd64)$")
  message(FATAL_ERROR "The native debugger supports x86_64 only")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(DISTORM_SOURCES
  Dependencies/distorm/src/decoder.c Dependencies/distorm/src/distorm.c
  Dependencies/distorm/src/instructions.c Dependencies/distorm/src/insts.c
  Dependencies/distorm/src/mnemonics.c Dependencies/distorm/src/operands.c
  Dependencies/distorm/src/prefix.c Dependencies/distorm/src/textdefs.c)
set(PLATFORM_SOURCES
  CloseRemoteProcess.cpp ControlRemoteProcess.cpp Debugger.cpp DisassembleCode.cpp
  EnumerateProcesses.cpp EnumerateRemoteSectionsAndModules.cpp Input.cpp
  IsProcessValid.cpp OpenRemoteProcess.cpp ReadRemoteMemory.cpp WriteRemoteMemory.cpp)
if(WIN32)
  set(PLATFORM_DIR Windows)
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  set(PLATFORM_DIR Unix)
else()
  message(FATAL_ERROR "Only Windows and Linux are supported")
endif()
list(APPEND PLATFORM_SOURCES AdvancedDebugger.cpp)
list(TRANSFORM PLATFORM_SOURCES PREPEND "${PLATFORM_DIR}/")
add_library(NativeCore SHARED ${PLATFORM_SOURCES} Shared/DistormHelper.cpp ${DISTORM_SOURCES})
target_include_directories(NativeCore PRIVATE Dependencies/distorm/include)
target_compile_definitions(NativeCore PRIVATE RECLASSNET64=1)
set_target_properties(NativeCore PROPERTIES PREFIX "" LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out" RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out")
if(WIN32)
  target_sources(NativeCore PRIVATE Windows/exports.def)
  target_compile_definitions(NativeCore PRIVATE UNICODE _UNICODE NOMINMAX _WIN32_WINNT=0x0601)
  target_link_libraries(NativeCore PRIVATE dinput8 dxguid psapi)
  if(MINGW)
    target_link_options(NativeCore PRIVATE -static -static-libgcc -static-libstdc++)
  endif()
else()
  target_link_libraries(NativeCore PRIVATE stdc++fs)
endif()
