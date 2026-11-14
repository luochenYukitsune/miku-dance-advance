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
add_library(mmd_bullet SHARED mmd_bullet.cpp "${SOFT_ROOT}/mmd_bullet_soft.cpp" ${BULLET_SOURCES} ${SOFT_SOURCES})
# The Bullet 2.75 profiler owns process-global mutable state, while each backend owns an independent world.
target_compile_definitions(mmd_bullet PRIVATE BT_NO_PROFILE=1)
if(WIN32)
  target_compile_definitions(mmd_bullet PRIVATE BT_HAS_ALIGNED_ALLOCATOR)
endif()
target_include_directories(mmd_bullet PRIVATE "${BULLET_ROOT}"
  "${CMAKE_CURRENT_BINARY_DIR}/compat/upstream"
  "${CMAKE_CURRENT_BINARY_DIR}/compat/upstream/BulletSoftBody")
if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
  target_compile_options(mmd_bullet PRIVATE -Wno-c++11-narrowing)
endif()
target_compile_features(mmd_bullet PRIVATE cxx_std_17)
set_target_properties(mmd_bullet PROPERTIES PREFIX "" OUTPUT_NAME "mmd_bullet")
