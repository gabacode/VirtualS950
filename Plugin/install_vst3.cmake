#
# Copy the built VST3 into the folder a host scans, and do not fail the build if it cannot.
#
# A DAW holds a plugin's binary open for as long as a set using it is loaded, so a rebuild
# while the host is running cannot replace the installed copy. That is ordinary - every
# plugin developer closes the host to rebuild - but JUCE's COPY_PLUGIN_AFTER_BUILD turns it
# into thirty lines of MSB3073 that bury the one sentence worth reading, and marks a build
# as failed when the build was fine and only the copy was not.
#
# So this does the copy and says plainly what happened. Invoked with -DSRC= and -DDST=.
#
if(NOT EXISTS "${SRC}")
    message(STATUS "VirtualS950: nothing to install - ${SRC} does not exist")
    return()
endif()

get_filename_component(BUNDLE "${SRC}" NAME)

execute_process(
    COMMAND "${CMAKE_COMMAND}" -E copy_directory "${SRC}" "${DST}/${BUNDLE}"
    RESULT_VARIABLE COPY_RESULT
    ERROR_VARIABLE  COPY_ERROR
    OUTPUT_QUIET)

if(COPY_RESULT EQUAL 0)
    message(STATUS "VirtualS950: installed to ${DST}/${BUNDLE}")
else()
    message(STATUS "")
    message(STATUS "  VirtualS950: the plugin BUILT but could not be INSTALLED.")
    message(STATUS "  Your host almost certainly has the old one loaded and is holding it open.")
    message(STATUS "  Close the DAW and build again - nothing is wrong with the build itself.")
    message(STATUS "")
    message(STATUS "    wanted: ${DST}/${BUNDLE}")
    message(STATUS "")
endif()
