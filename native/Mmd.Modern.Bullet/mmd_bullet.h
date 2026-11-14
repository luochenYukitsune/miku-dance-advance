#pragma once
#include <stdint.h>
#if defined(_WIN32)
#define MMD_EXPORT __declspec(dllexport)
#else
#define MMD_EXPORT __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
#define MMD_NOEXCEPT noexcept
extern "C" {
#else
#define MMD_NOEXCEPT
#endif
// ABI v4 adds incremental membership/temporary modes; layouts remain float32 and quaternion XYZW.
typedef struct { float x, y, z; } MmdBulletVector;
typedef struct { MmdBulletVector position; float qx, qy, qz, qw; } MmdBulletPose;
typedef struct {
    int32_t shape, mode, collision_group, collision_mask;
    MmdBulletPose pose;
    MmdBulletVector size;
    float mass, linear_damping, angular_damping, restitution, friction;
} MmdBulletBody;
typedef struct {
    int32_t body_a, body_b, type;
    MmdBulletPose frame_a, frame_b;
    MmdBulletVector linear_lower, linear_upper, angular_lower, angular_upper, linear_spring, angular_spring;
} MmdBulletJoint;
typedef struct { int32_t abi_version, bullet_version, scalar_bytes, pose_bytes, body_bytes, joint_bytes; } MmdBulletAbi;
MMD_EXPORT int32_t mmd_bullet_get_abi(MmdBulletAbi* result) MMD_NOEXCEPT;
MMD_EXPORT const char* mmd_bullet_last_error(void) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_create(MmdBulletVector gravity, int32_t has_ground, float ground_y, void** result) MMD_NOEXCEPT;
MMD_EXPORT void mmd_bullet_destroy(void* context) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_add_body(void* context, const MmdBulletBody* description) MMD_NOEXCEPT;
// 0 success; 1 invalid/failed operation; 2 unsupported joint type.
MMD_EXPORT int32_t mmd_bullet_add_joint(void* context, const MmdBulletJoint* description) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_set_kinematic_target(void* context, int32_t index, const MmdBulletPose* target) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_get_body_pose(void* context, int32_t index, MmdBulletPose* result) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_apply_impulse(void* context, int32_t index, MmdBulletVector linear, MmdBulletVector angular) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_set_gravity(void* context, MmdBulletVector gravity) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_step(void* context, float seconds) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_add_body_slot(void* context, const MmdBulletBody* description, int32_t* result) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_add_joint_slot(void* context, const MmdBulletJoint* description, int32_t* result) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_remove_joint(void* context, int32_t index) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_remove_body(void* context, int32_t index) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_reset_body(void* context, int32_t index, const MmdBulletPose* target) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_set_body_kinematic(void* context, int32_t index, int32_t enabled) MMD_NOEXCEPT;
// Additive ABI4 feature bit 2: original MMD joint pull safeguard, preserving actual body basis and contacts.
MMD_EXPORT int32_t mmd_bullet_correct_joint_overextension(void* context, int32_t body_a, int32_t body_b, float maximum_distance) MMD_NOEXCEPT;
// Additive ABI4 feature bit 1: explicit actual-body soft-rigid world.
MMD_EXPORT int32_t mmd_bullet_features(void) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_create_coupled(MmdBulletVector gravity, int32_t has_ground, float ground_y, void** result) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_soft_create(void* context, const int32_t* rigid_slots, int32_t count, void** result) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_soft_destroy(void* context, void* binding) MMD_NOEXCEPT;
MMD_EXPORT int32_t mmd_bullet_get_body_velocity(void* context, int32_t index, MmdBulletVector* linear, MmdBulletVector* angular) MMD_NOEXCEPT;
#ifdef __cplusplus
}
#endif
