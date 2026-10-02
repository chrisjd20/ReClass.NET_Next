cmake_minimum_required(VERSION 3.19)
project(ReClassBreakout LANGUAGES C CXX)

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8 OR NOT CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64|amd64)$")
  message(FATAL_ERROR "ReClass: Breakout supports Windows/Linux x64 only")
endif()
if(WIN32)
  set(CMAKE_ASM_NASM_OBJECT_FORMAT win64)
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  set(CMAKE_ASM_NASM_OBJECT_FORMAT elf64)
else()
  message(FATAL_ERROR "Only Windows and Linux are supported")
endif()
enable_language(ASM_NASM)
find_package(Python3 REQUIRED COMPONENTS Interpreter)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# The manifest is the single dependency pin, also copied into both packages.
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/../../Dependencies/demo-dependencies.json" DEMO_DEPENDENCIES)
string(JSON RAYLIB_SOURCE_URL GET "${DEMO_DEPENDENCIES}" raylib source_url)
string(JSON RAYLIB_SOURCE_SHA256 GET "${DEMO_DEPENDENCIES}" raylib source_sha256)
include(FetchContent)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(CUSTOMIZE_BUILD ON CACHE BOOL "" FORCE)
# 5.5 compiles raudio.c according to this config.h flag. USE_AUDIO alone
# controls ancillary platform linkage and does not disable the audio module.
set(SUPPORT_MODULE_RAUDIO OFF CACHE BOOL "" FORCE)
set(USE_AUDIO OFF CACHE BOOL "" FORCE)
set(PLATFORM Desktop CACHE STRING "" FORCE)
set(OPENGL_VERSION 3.3 CACHE STRING "" FORCE)
set(USE_EXTERNAL_GLFW OFF CACHE STRING "" FORCE)
set(GLFW_BUILD_X11 ON CACHE BOOL "" FORCE)
set(GLFW_BUILD_WAYLAND OFF CACHE BOOL "" FORCE)
FetchContent_Declare(raylib URL "${RAYLIB_SOURCE_URL}" URL_HASH "SHA256=${RAYLIB_SOURCE_SHA256}")
FetchContent_MakeAvailable(raylib)

set(GENERATED "${CMAKE_CURRENT_BINARY_DIR}/generated")
file(MAKE_DIRECTORY "${GENERATED}")
add_custom_command(
  OUTPUT "${GENERATED}/fonts.generated.h"
  COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/tools/embed_fonts.py"
    --assets "${CMAKE_CURRENT_SOURCE_DIR}/assets/fonts"
    --manifest "${CMAKE_CURRENT_SOURCE_DIR}/../../Dependencies/demo-dependencies.json"
    --output "${GENERATED}/fonts.generated.h"
  DEPENDS tools/embed_fonts.py ../../Dependencies/demo-dependencies.json
    assets/fonts/LiberationSans-Regular.ttf assets/fonts/LiberationSans-Bold.ttf
    assets/fonts/LiberationMono-Regular.ttf
  VERBATIM)
add_custom_command(
  OUTPUT "${GENERATED}/layout.h" "${GENERATED}/layout.inc" "${GENERATED}/layout.md"
  COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/tools/generate_layout.py"
    --schema "${CMAKE_CURRENT_SOURCE_DIR}/tools/layout.json" --output-dir "${GENERATED}" --guide "${GENERATED}/layout.md"
  DEPENDS tools/generate_layout.py tools/layout.json
  VERBATIM)
add_custom_command(
  OUTPUT "${GENERATED}/lessons.generated.cpp" "${GENERATED}/GUIDE.html" "${GENERATED}/GUIDE.md"
  COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/tools/generate-lessons.py"
    --input "${CMAKE_CURRENT_SOURCE_DIR}/content/lessons.json" --output-dir "${GENERATED}"
    --layout "${CMAKE_CURRENT_SOURCE_DIR}/tools/layout.json"
  DEPENDS tools/generate-lessons.py content/lessons.json tools/layout.json src/tutorial.h
  VERBATIM)
add_custom_target(breakout-content DEPENDS
  "${GENERATED}/fonts.generated.h"
  "${GENERATED}/layout.h" "${GENERATED}/layout.inc" "${GENERATED}/layout.md"
  "${GENERATED}/lessons.generated.cpp" "${GENERATED}/GUIDE.html" "${GENERATED}/GUIDE.md")
add_executable(ReClassBreakout src/main.cpp src/frontend.cpp src/ui.cpp src/selection.cpp src/scene.cpp src/sprites.cpp src/progress.cpp src/game.cpp src/teaching.asm "${GENERATED}/lessons.generated.cpp")
add_dependencies(ReClassBreakout breakout-content)
target_include_directories(ReClassBreakout PRIVATE src "${CMAKE_CURRENT_BINARY_DIR}" "${GENERATED}")
set_source_files_properties(src/teaching.asm PROPERTIES
  COMPILE_OPTIONS "-I${GENERATED}/"
  OBJECT_DEPENDS "${GENERATED}/layout.inc")
target_link_libraries(ReClassBreakout PRIVATE raylib)
set_target_properties(ReClassBreakout PROPERTIES
  INTERPROCEDURAL_OPTIMIZATION FALSE
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/out")
if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
  # Learner-facing data remains authoritative across pauses and external edits.
  set_source_files_properties(src/game.cpp PROPERTIES COMPILE_OPTIONS "-O0;-fno-lto;-fno-inline")
  target_compile_options(ReClassBreakout PRIVATE "$<$<COMPILE_LANGUAGE:CXX>:-g;-fno-lto>")
  if(WIN32)
    target_compile_definitions(ReClassBreakout PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_options(ReClassBreakout PRIVATE -static -static-libgcc -static-libstdc++ -Wl,--dynamicbase -Wl,--nxcompat)
  else()
    target_compile_options(ReClassBreakout PRIVATE "$<$<COMPILE_LANGUAGE:CXX>:-fPIE>")
    target_link_options(ReClassBreakout PRIVATE -pie -Wl,-z,noexecstack)
    target_link_libraries(ReClassBreakout PRIVATE dl)
  endif()
endif()
install(TARGETS ReClassBreakout RUNTIME DESTINATION Demo)
install(FILES "${GENERATED}/GUIDE.html" "${GENERATED}/GUIDE.md" "${GENERATED}/layout.md" DESTINATION Demo)
