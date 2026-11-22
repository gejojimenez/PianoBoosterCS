# RefDump: the original C++ core of PianoBooster, without its interface, writing what it
# does with a MIDI file (see README.md). Build it with build.sh from an MSYS2 UCRT64 shell.
cmake_minimum_required(VERSION 3.16)
project(refdump CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)

set(PB_SRC "${CMAKE_CURRENT_SOURCE_DIR}/../../../PianoBooster/src" CACHE PATH "The src folder of the original PianoBooster")
get_filename_component(PB_SRC "${PB_SRC}" ABSOLUTE)
if(NOT EXISTS "${PB_SRC}/Conductor.cpp")
    message(FATAL_ERROR "PianoBooster sources not found in ${PB_SRC}")
endif()

# The original files are copied next to the stubs, because a quoted #include looks in the
# folder of the file first and would find the real Settings.h and MidiDevice.h.
set(WORK "${CMAKE_BINARY_DIR}/pbsrc")
file(MAKE_DIRECTORY "${WORK}")

set(ORIGINAL_SOURCES
    Bar Cfg Chord Conductor Draw MidiFile MidiTrack Merge Notation Piano Rating Score
    Scroll Song SongMap StavePosition Tempo TrackList Util)

file(GLOB ORIGINAL_HEADERS "${PB_SRC}/*.h")
foreach(header ${ORIGINAL_HEADERS})
    get_filename_component(name "${header}" NAME)
    configure_file("${header}" "${WORK}/${name}" COPYONLY)
endforeach()
set(SOURCES)
foreach(name ${ORIGINAL_SOURCES})
    configure_file("${PB_SRC}/${name}.cpp" "${WORK}/${name}.cpp" COPYONLY)
    list(APPEND SOURCES "${WORK}/${name}.cpp")
endforeach()
# Draw.cpp does not compile without FTGL (NO_USE_FTGL): two float literals end in "ff".
# That code only draws the note names, which RefDump never does.
file(READ "${WORK}/Draw.cpp" DRAW_CPP)
string(REPLACE "2.07925ff" "2.07925f" DRAW_CPP "${DRAW_CPP}")
string(REPLACE "4.433665ff" "4.433665f" DRAW_CPP "${DRAW_CPP}")
file(WRITE "${WORK}/Draw.cpp" "${DRAW_CPP}")

foreach(stub Settings.h MidiDevice.h)
    configure_file("${CMAKE_CURRENT_SOURCE_DIR}/stubs/${stub}" "${WORK}/${stub}" COPYONLY)
endforeach()

find_package(Qt6 REQUIRED COMPONENTS Core Gui Widgets)
find_package(OpenGL REQUIRED)

add_executable(refdump
    RefDump.cpp
    stubs/StubsImpl.cpp
    ${SOURCES}
    "${WORK}/Draw.h"
    "${WORK}/TrackList.h")
target_include_directories(refdump PRIVATE "${WORK}")
target_compile_definitions(refdump PRIVATE NO_USE_FTGL PREFIX="refdump" DATA_DIR="data")
target_compile_options(refdump PRIVATE -O2 -w)
target_link_libraries(refdump PRIVATE Qt6::Core Qt6::Gui Qt6::Widgets ${OPENGL_LIBRARIES} glu32)
