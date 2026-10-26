# Builds the vendored rcheevos (RetroAchievements' MIT-licensed runtime) as a shared library for
# GameVault's P/Invoke binding (docs/plans/phase-10.md). Only the parts GameVault uses are compiled:
# rc_client, the runtime, the API and MD5. Games are hashed in C#, so rhash's disc readers are left out.
cmake_minimum_required(VERSION 3.16)
project(rcheevos C)

set(RC rcheevos)
add_library(rcheevos SHARED
  ${RC}/src/rc_client.c
  ${RC}/src/rc_compat.c
  ${RC}/src/rc_util.c
  ${RC}/src/rc_version.c
  ${RC}/src/rapi/rc_api_common.c
  ${RC}/src/rapi/rc_api_editor.c
  ${RC}/src/rapi/rc_api_info.c
  ${RC}/src/rapi/rc_api_runtime.c
  ${RC}/src/rapi/rc_api_user.c
  ${RC}/src/rcheevos/alloc.c
  ${RC}/src/rcheevos/condition.c
  ${RC}/src/rcheevos/condset.c
  ${RC}/src/rcheevos/consoleinfo.c
  ${RC}/src/rcheevos/format.c
  ${RC}/src/rcheevos/lboard.c
  ${RC}/src/rcheevos/memref.c
  ${RC}/src/rcheevos/operand.c
  ${RC}/src/rcheevos/rc_validate.c
  ${RC}/src/rcheevos/richpresence.c
  ${RC}/src/rcheevos/runtime.c
  ${RC}/src/rcheevos/runtime_progress.c
  ${RC}/src/rcheevos/trigger.c
  ${RC}/src/rcheevos/value.c
  ${RC}/src/rhash/md5.c
)
target_include_directories(rcheevos PRIVATE ${RC}/include ${RC}/src)
target_compile_definitions(rcheevos PRIVATE RC_SHARED)
set_target_properties(rcheevos PROPERTIES C_VISIBILITY_PRESET hidden PREFIX "lib")
# GameVault's build passes GV_OUT so every generator (single- or multi-config) puts the library in one place.
if(GV_OUT)
  foreach(kind RUNTIME LIBRARY)
    set_target_properties(rcheevos PROPERTIES ${kind}_OUTPUT_DIRECTORY ${GV_OUT})
    foreach(config DEBUG RELEASE RELWITHDEBINFO MINSIZEREL)
      set_target_properties(rcheevos PROPERTIES ${kind}_OUTPUT_DIRECTORY_${config} ${GV_OUT})
    endforeach()
  endforeach()
endif()
if(UNIX)
  target_link_libraries(rcheevos PRIVATE m)
endif()
if(WIN32)
  set_target_properties(rcheevos PROPERTIES PREFIX "")
  target_compile_definitions(rcheevos PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()
