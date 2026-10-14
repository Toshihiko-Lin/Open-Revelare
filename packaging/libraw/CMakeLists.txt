cmake_minimum_required(VERSION 3.21)
project(OpenRevelareRaw LANGUAGES C CXX)
include(FetchContent)
include(ExternalProject)
if(POLICY CMP0135)
  cmake_policy(SET CMP0135 NEW)
endif()
if(POLICY CMP0169)
  cmake_policy(SET CMP0169 OLD)
endif()
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_STANDARD 11)
# Keep the Windows self-contained distribution independent of a VC++ redistributable.
# All native allocations are released inside this same module.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
FetchContent_Declare(libraw
  URL https://www.libraw.org/data/LibRaw-0.22.2.tar.gz
  URL_HASH SHA256=de86b035655accff8d4010f1a221fdf50d353cb7b1422ba26f14a0db92612cfa)
FetchContent_GetProperties(libraw)
if(NOT libraw_POPULATED)
  FetchContent_Populate(libraw)
endif()
# Static JPEG avoids platform-specific dependency loading. SIMD is optional;
# disabling it keeps builds independent of an assembler installation.
FetchContent_Declare(jpeg
  URL https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.1.2/libjpeg-turbo-3.1.2.tar.gz
  URL_HASH SHA256=8f0012234b464ce50890c490f18194f913a7b1f4e6a03d6644179fa0f867d0cf)
FetchContent_GetProperties(jpeg)
if(NOT jpeg_POPULATED)
  FetchContent_Populate(jpeg)
endif()
set(JPEG_BUILD "${CMAKE_BINARY_DIR}/jpeg-build")
if(MSVC)
  set(JPEG_LIBRARY "${JPEG_BUILD}/Release/jpeg-static.lib")
else()
  set(JPEG_LIBRARY "${JPEG_BUILD}/libjpeg.a")
endif()
ExternalProject_Add(raw_jpeg_build
  SOURCE_DIR "${jpeg_SOURCE_DIR}" BINARY_DIR "${JPEG_BUILD}"
  CMAKE_ARGS -DENABLE_SHARED=OFF -DENABLE_STATIC=ON -DWITH_SIMD=OFF
    -DWITH_TURBOJPEG=OFF -DWITH_CRT_DLL=OFF -DBUILD=20250827 -DCMAKE_C_VISIBILITY_PRESET=hidden
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DCMAKE_BUILD_TYPE=Release
  BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --config Release --target jpeg-static --parallel 4
  INSTALL_COMMAND ""
  BUILD_BYPRODUCTS "${JPEG_LIBRARY}")
add_library(raw_jpeg STATIC IMPORTED)
set_target_properties(raw_jpeg PROPERTIES IMPORTED_LOCATION "${JPEG_LIBRARY}")
add_dependencies(raw_jpeg raw_jpeg_build)
FetchContent_Declare(lcms
  URL https://github.com/mm2/Little-CMS/releases/download/lcms2.19.1/lcms2-2.19.1.tar.gz
  URL_HASH SHA256=bfc54f7bab59fbc921012014a8032e4cba4abd46db47d46b76416a8c0b2815c8)
FetchContent_GetProperties(lcms)
if(NOT lcms_POPULATED)
  FetchContent_Populate(lcms)
endif()
file(GLOB LCMS_SOURCES "${lcms_SOURCE_DIR}/src/*.c")
add_library(raw_lcms STATIC ${LCMS_SOURCES})
set_target_properties(raw_lcms PROPERTIES C_VISIBILITY_PRESET hidden)
target_include_directories(raw_lcms PUBLIC "${lcms_SOURCE_DIR}/include")
file(GLOB_RECURSE RAW_SOURCES "${libraw_SOURCE_DIR}/src/*.cpp")
# Upstream's *_ph.cpp files are alternate placeholder implementations, not part
# of the full decoder build (see upstream Makefile.am / Makefile.msvc).
list(FILTER RAW_SOURCES EXCLUDE REGEX "_ph\\.cpp$")
add_library(OpenRevelare.Raw.Native SHARED bridge.cpp ${RAW_SOURCES})
target_include_directories(OpenRevelare.Raw.Native PRIVATE
  "${libraw_SOURCE_DIR}" "${jpeg_SOURCE_DIR}/src" "${JPEG_BUILD}")
target_compile_definitions(OpenRevelare.Raw.Native PRIVATE
  LIBRAW_NODLL USE_JPEG USE_LCMS2)
# LIBRAW_NOTHREADS is deliberately absent: this is the thread-safe build.
target_link_libraries(OpenRevelare.Raw.Native PRIVATE raw_jpeg raw_lcms)
find_package(Threads REQUIRED)
target_link_libraries(OpenRevelare.Raw.Native PRIVATE Threads::Threads)
if(WIN32)
  target_link_libraries(OpenRevelare.Raw.Native PRIVATE ws2_32)
endif()
if(MSVC)
  target_compile_options(OpenRevelare.Raw.Native PRIVATE /EHsc /utf-8 /MP)
endif()
set_target_properties(OpenRevelare.Raw.Native PROPERTIES
  CXX_VISIBILITY_PRESET hidden C_VISIBILITY_PRESET hidden VISIBILITY_INLINES_HIDDEN YES)
install(TARGETS OpenRevelare.Raw.Native RUNTIME DESTINATION . COMPONENT Raw LIBRARY DESTINATION . COMPONENT Raw)
