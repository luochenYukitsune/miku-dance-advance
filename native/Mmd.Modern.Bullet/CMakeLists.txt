cmake_minimum_required(VERSION 3.16)
project(MmdModernBullet LANGUAGES CXX)
set(BULLET_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../third_party/bullet-2.75/src")
file(GLOB_RECURSE BULLET_SOURCES CONFIGURE_DEPENDS
  "${BULLET_ROOT}/BulletCollision/*.cpp" "${BULLET_ROOT}/BulletDynamics/*.cpp" "${BULLET_ROOT}/LinearMath/*.cpp")
list(FILTER BULLET_SOURCES EXCLUDE REGEX "/Bullet-C-API\\.cpp$")
set(SOFT_ROOT "${CMAKE_CURRENT_LIST_DIR}/../Mmd.Modern.Bullet.SoftBody")
find_package(Python3 REQUIRED COMPONENTS Interpreter)
execute_process(COMMAND "${Python3_EXECUTABLE}"
  "${CMAKE_CURRENT_LIST_DIR}/../../scripts/bullet_soft_sources.py"
  --native "${SOFT_ROOT}" --output "${CMAKE_CURRENT_BINARY_DIR}"
  RESULT_VARIABLE SOFT_PREPARE_RESULT)
if(NOT SOFT_PREPARE_RESULT EQUAL 0)
  message(FATAL_ERROR "Preparing recorded Bullet soft compatibility patches failed")
endif()
file(GLOB SOFT_SOURCES CONFIGURE_DEPENDS "${CMAKE_CURRENT_BINARY_DIR}/compat/upstream/BulletSoftBody/*.cpp")
# Match scripts/bullet_soft_sources.py prepared_compile_files(): the MMD binary32 evaluation-order copies of the
# sequential-impulse solver and GJK/EPA replace the pristine third_party units, otherwise this build produces
# different contact and EPA results from the verified Python/Zig build.
set(COMPAT_SOLVER "${CMAKE_CURRENT_BINARY_DIR}/compat/upstream/BulletDynamics/ConstraintSolver/btSequentialImpulseConstraintSolver.cpp")
set(COMPAT_EPA "${CMAKE_CURRENT_BINARY_DIR}/compat/upstream/BulletCollision/NarrowPhaseCollision/btGjkEpa2.cpp")
foreach(COMPAT_UNIT IN ITEMS "${COMPAT_SOLVER}" "${COMPAT_EPA}")
  if(NOT EXISTS "${COMPAT_UNIT}")
    message(FATAL_ERROR "Prepared Bullet compatibility unit is missing: ${COMPAT_UNIT}")
  endif()
endforeach()
list(FILTER BULLET_SOURCES EXCLUDE REGEX "/btSequentialImpulseConstraintSolver\\.cpp$")
list(FILTER BULLET_SOURCES EXCLUDE REGEX "/btGjkEpa2\\.cpp$")
list(APPEND BULLET_SOURCES "${COMPAT_SOLVER}" "${COMPAT_EPA}")
add_library(mmd_bullet SHARED mmd_bullet.cpp "${SOFT_ROOT}/mmd_bullet_soft.cpp" ${BULLET_SOURCES} ${SOFT_SOURCES})
# The Bullet 2.75 profiler owns process-global mutable state, while each backend owns an independent world.
target_compile_definitions(mmd_bullet PRIVATE BT_NO_PROFILE=1 NDEBUG)
if(WIN32)
  target_compile_definitions(mmd_bullet PRIVATE BT_HAS_ALIGNED_ALLOCATOR)
endif()
target_include_directories(mmd_bullet PRIVATE "${BULLET_ROOT}"
  "${CMAKE_CURRENT_BINARY_DIR}/compat/upstream"
  "${CMAKE_CURRENT_BINARY_DIR}/compat/upstream/BulletSoftBody")
if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
  target_compile_options(mmd_bullet PRIVATE -Wno-c++11-narrowing)
endif()
# Same strict float model as the Python/Zig build (no fast-math, no FMA contraction).
if(MSVC)
  target_compile_options(mmd_bullet PRIVATE /fp:precise)
else()
  target_compile_options(mmd_bullet PRIVATE -fno-fast-math -ffp-contract=off)
endif()
target_compile_features(mmd_bullet PRIVATE cxx_std_17)
set_target_properties(mmd_bullet PROPERTIES PREFIX "" OUTPUT_NAME "mmd_bullet")
