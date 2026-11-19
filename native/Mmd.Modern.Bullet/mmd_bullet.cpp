#include "mmd_bullet.h"
// PMX joint-field mapping adapted from nanoem/ext/physics_bullet.cc @
// 30acffaa29f5d2eb9e997d69418f2e4b97b5894f (MIT/X11).
// Copyright and full permission notice: ../../src/Mmd.Modern.Physics.Bullet/LICENSE.Nanoem.txt.
#include <BulletCollision/BroadphaseCollision/btAxisSweep3.h>
#include <btBulletDynamicsCommon.h>
#include "mmd_bullet_parallel.h"
#include "../Mmd.Modern.Bullet.SoftBody/mmd_soft_plane_collision.h"
#include "../Mmd.Modern.Bullet.SoftBody/mmd_soft_attachment.h"
#include <algorithm>
#include <BulletDynamics/ConstraintSolver/btGeneric6DofSpringConstraint.h>
#include <cmath>
#include <cstdio>
#include <exception>
#include <memory>
#include <stdexcept>
#include <vector>

static_assert(sizeof(btScalar) == sizeof(float), "This ABI requires Bullet float32.");
static_assert(sizeof(MmdBulletPose) == 28 && sizeof(MmdBulletBody) == 76 && sizeof(MmdBulletJoint) == 140
    && sizeof(MmdBulletKinematicTargetItem) == 36 && sizeof(MmdBulletJointPullItem) == 12, "Unexpected ABI layout.");
namespace {
thread_local char last_error[512] = {};
void error(const char* message) noexcept { std::snprintf(last_error, sizeof(last_error), "%s", message); }
template<class F> int32_t guarded(F function) noexcept {
    last_error[0] = '\0';
    try { function(); return 0; }
    catch (const std::exception& exception) { error(exception.what()); }
    catch (...) { error("Unknown native Bullet failure."); }
    return 1;
}
btVector3 vector(MmdBulletVector v) { return btVector3(v.x, v.y, v.z); }
bool finite(MmdBulletVector v) { return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z); }
btTransform transform(const MmdBulletPose& p) {
    btQuaternion q(p.qx, p.qy, p.qz, p.qw);
    if (!finite(p.position) || !std::isfinite(q.length2()) || btFabs(q.length2() - 1) > .001f)
        throw std::invalid_argument("Invalid rigid pose.");
    return btTransform(q.normalized(), vector(p.position));
}
MmdBulletPose pose(const btTransform& t) {
    const btVector3& p = t.getOrigin(); btQuaternion q = t.getRotation();
    return {{p.x(), p.y(), p.z()}, q.x(), q.y(), q.z(), q.w()};
}
struct Body {
    std::unique_ptr<btCollisionShape> shape;
    std::unique_ptr<btDefaultMotionState> motion;
    std::unique_ptr<btRigidBody> body;
    int mode;
    btScalar authored_mass = 0;
    btVector3 authored_inertia = btVector3(0, 0, 0);
    short group = 1, mask = -1;
    bool temporary_kinematic = false;
};
struct Context {
    std::unique_ptr<btDefaultCollisionConfiguration> configuration;
    std::unique_ptr<btCollisionDispatcher> dispatcher;
    std::unique_ptr<btDbvtBroadphase> broadphase;
    std::unique_ptr<btSequentialImpulseConstraintSolver> solver;
    std::unique_ptr<btDiscreteDynamicsWorld> world;
    btSoftRigidDynamicsWorld* softWorld = nullptr;
    std::vector<void*> softBindings;
    std::vector<std::unique_ptr<Body>> bodies;
    std::vector<std::unique_ptr<btTypedConstraint>> constraints;
    std::unique_ptr<Body> ground;
    Context(MmdBulletVector gravity, bool has_ground, float ground_y, bool coupled = false) {
        if(coupled)configuration.reset(new mmd_soft::PlaneSoftConfiguration());
        else configuration.reset(new btDefaultCollisionConfiguration());
        if(coupled)dispatcher.reset(new btCollisionDispatcher(configuration.get()));
        else dispatcher.reset(new mmd_bullet_parallel::ParallelCollisionDispatcher(configuration.get()));
        broadphase.reset(new btDbvtBroadphase());
        if(coupled)solver.reset(new btSequentialImpulseConstraintSolver());
        else solver.reset(new mmd_bullet_parallel::ParallelIslandSolver());
        if(coupled){
            softWorld=new mmd_soft::CoupledDynamicsWorld(dispatcher.get(),broadphase.get(),solver.get(),configuration.get());
            world.reset(softWorld);auto& info=softWorld->getWorldInfo();
            info.m_gravity=vector(gravity);info.m_broadphase=broadphase.get();info.m_dispatcher=dispatcher.get();
            info.air_density=1.2f;info.water_density=0;info.water_offset=0;info.water_normal.setZero();info.m_sparsesdf.Initialize();
        }else world.reset(new btDiscreteDynamicsWorld(dispatcher.get(), broadphase.get(), solver.get(), configuration.get()));
        world->setGravity(vector(gravity));
        if (has_ground) {
            ground.reset(new Body());
            ground->shape.reset(new btStaticPlaneShape(btVector3(0, 1, 0), 0));
            ground->motion.reset(new btDefaultMotionState(btTransform(btQuaternion::getIdentity(), btVector3(0, ground_y, 0))));
            btRigidBody::btRigidBodyConstructionInfo info(0, ground->motion.get(), ground->shape.get(), btVector3(0, 0, 0));
            ground->body.reset(new btRigidBody(info));
            if (!coupled) ground->body->setUserPointer(&mmd_bullet_parallel::horizontalGround);
            world->addRigidBody(ground->body.get());
        }
    }
    ~Context() {
        if (!world) return;
        for(void* binding:softBindings)mmd_soft::destroyBorrowed(binding);softBindings.clear();
        if(softWorld)softWorld->getWorldInfo().m_sparsesdf.Reset();
        for (auto& constraint : constraints) if (constraint) world->removeConstraint(constraint.get());
        constraints.clear();
        for (auto& body : bodies) if (body) world->removeRigidBody(body->body.get());
        if (ground) world->removeRigidBody(ground->body.get());
        bodies.clear(); ground.reset();
    }
};
Context& context(void* pointer) { if (!pointer) throw std::invalid_argument("Null Bullet context."); return *static_cast<Context*>(pointer); }
Body& body(Context& c, int32_t index) {
    if (index < 0 || static_cast<size_t>(index) >= c.bodies.size() || !c.bodies[index]) throw std::out_of_range("Body index out of range or removed.");
    return *c.bodies[index];
}
void set_body_kinematic_impl(Context& c, Body& value, int32_t enabled) {
    if (enabled != 0 && enabled != 1) throw std::invalid_argument("Invalid kinematic switch.");
    bool temporary = enabled != 0 && value.mode != 0;
    if (temporary == value.temporary_kinematic) return;
    c.world->removeRigidBody(value.body.get());
    value.temporary_kinematic = temporary;
    int flags = value.body->getCollisionFlags() & ~(btCollisionObject::CF_STATIC_OBJECT | btCollisionObject::CF_KINEMATIC_OBJECT);
    bool kinematic = value.mode == 0 || temporary;
    value.body->setMassProps(kinematic ? 0 : value.authored_mass, kinematic ? btVector3(0, 0, 0) : value.authored_inertia);
    if (kinematic) flags |= btCollisionObject::CF_KINEMATIC_OBJECT;
    else if (value.authored_mass == 0) flags |= btCollisionObject::CF_STATIC_OBJECT;
    value.body->setCollisionFlags(flags); value.body->updateInertiaTensor();
    value.body->clearForces(); value.body->setLinearVelocity(btVector3(0, 0, 0)); value.body->setAngularVelocity(btVector3(0, 0, 0));
    value.body->setInterpolationWorldTransform(value.body->getWorldTransform());
    value.body->setActivationState(DISABLE_DEACTIVATION);
    c.world->addRigidBody(value.body.get(), value.group, value.mask);
}
void set_kinematic_target_impl(Body& value, const MmdBulletPose& target) {
    if (value.mode != 0 && !value.temporary_kinematic) throw std::invalid_argument("Expected kinematic body and target pose.");
    btTransform t = transform(target); value.motion->setWorldTransform(t); value.body->setWorldTransform(t); value.body->activate(true);
}
void correct_joint_overextension_impl(Context& c, int32_t body_a, int32_t body_b, float maximum_distance) {
    auto& a = *body(c, body_a).body; auto& b = *body(c, body_b).body;
    if (body_a == body_b || !std::isfinite(maximum_distance) || maximum_distance < 0)
        throw std::invalid_argument("Invalid joint pull correction.");
    const btVector3& pa = a.getWorldTransform().getOrigin();
    const btVector3& pb = b.getWorldTransform().getOrigin();
    const float dx = pa.x() - pb.x(), dy = pa.y() - pb.y(), dz = pa.z() - pb.z();
    const float distance = std::sqrt((dx * dx + dy * dy) + dz * dz);
    if (distance - maximum_distance >= 2.0f) {
        const float ratio = maximum_distance / distance;
        // Original C3460 mutates the simulation transform only, preserving its 3x3 basis,
        // motion/interpolation state, contact cache and accumulated forces.
        b.getWorldTransform().getOrigin().setValue(pa.x() + (pb.x() - pa.x()) * ratio,
            pa.y() + (pb.y() - pa.y()) * ratio, pa.z() + (pb.z() - pa.z()) * ratio);
        b.setLinearVelocity(btVector3(0, 0, 0)); b.setAngularVelocity(btVector3(0, 0, 0));
    }
}
}
int32_t mmd_bullet_get_abi(MmdBulletAbi* result) noexcept {
    if (!result) { error("Null ABI result."); return 1; }
    *result = {4, 275, sizeof(btScalar), sizeof(MmdBulletPose), sizeof(MmdBulletBody), sizeof(MmdBulletJoint)}; return 0;
}
const char* mmd_bullet_last_error() noexcept { return last_error; }
int32_t mmd_bullet_create(MmdBulletVector gravity, int32_t has_ground, float ground_y, void** result) noexcept {
    if (result) *result = nullptr;
    return guarded([&] {
        if (!result || !finite(gravity) || !std::isfinite(ground_y)) throw std::invalid_argument("Invalid world settings.");
        *result = new Context(gravity, has_ground != 0, ground_y);
    });
}
void mmd_bullet_destroy(void* pointer) noexcept { try { delete static_cast<Context*>(pointer); } catch (...) { error("Bullet context disposal failed."); } }
int32_t mmd_bullet_add_body(void* pointer, const MmdBulletBody* d) noexcept {
    return guarded([&] {
        Context& c = context(pointer);
        if (!d || d->mode < 0 || d->mode > 2 || d->collision_group < 0 || d->collision_group > 15 || d->collision_mask < 0 || d->collision_mask > 65535
            || !finite(d->size) || d->size.x <= 0 || !std::isfinite(d->mass) || d->mass < 0
            || !std::isfinite(d->linear_damping) || !std::isfinite(d->angular_damping) || !std::isfinite(d->restitution) || !std::isfinite(d->friction))
            throw std::invalid_argument("Invalid body description.");
        std::unique_ptr<Body> value(new Body()); value->mode = d->mode;
        switch (d->shape) {
            case 0:
                value->shape.reset(c.softWorld
                    ? static_cast<btCollisionShape*>(new btSphereShape(d->size.x))
                    : new mmd_bullet_parallel::ImmutableShape<btSphereShape>(d->size.x));
                break;
            case 1:
                if (d->size.y <= 0 || d->size.z <= 0) throw std::invalid_argument("Invalid box half extents.");
                value->shape.reset(c.softWorld
                    ? static_cast<btCollisionShape*>(new btBoxShape(vector(d->size)))
                    : new mmd_bullet_parallel::ImmutableShape<btBoxShape>(vector(d->size)));
                break;
            case 2:
                if (d->size.y < 0) throw std::invalid_argument("Invalid capsule cylinder height.");
                value->shape.reset(c.softWorld
                    ? static_cast<btCollisionShape*>(new btCapsuleShape(d->size.x, d->size.y))
                    : new mmd_bullet_parallel::ImmutableShape<btCapsuleShape>(d->size.x, d->size.y));
                break;
            default: throw std::invalid_argument("Unknown shape.");
        }
        const btScalar mass = d->mode == 0 ? 0 : d->mass;
        btVector3 inertia(0, 0, 0); if (mass > 0) value->shape->calculateLocalInertia(mass, inertia);
        value->authored_mass = mass; value->authored_inertia = inertia;
        value->group = static_cast<short>(1 << d->collision_group); value->mask = static_cast<short>(d->collision_mask);
        value->motion.reset(new btDefaultMotionState(transform(d->pose)));
        btRigidBody::btRigidBodyConstructionInfo info(mass, value->motion.get(), value->shape.get(), inertia);
        info.m_linearDamping = d->linear_damping; info.m_angularDamping = d->angular_damping;
        info.m_restitution = d->restitution; info.m_friction = d->friction;
        value->body.reset(new btRigidBody(info));
        if (d->mode == 0) {
            value->body->setCollisionFlags((value->body->getCollisionFlags() & ~btCollisionObject::CF_STATIC_OBJECT) | btCollisionObject::CF_KINEMATIC_OBJECT);
            value->body->setActivationState(DISABLE_DEACTIVATION);
        } else value->body->setActivationState(DISABLE_DEACTIVATION);
        c.bodies.push_back(std::move(value));
        c.world->addRigidBody(c.bodies.back()->body.get(), static_cast<short>(1 << d->collision_group), static_cast<short>(d->collision_mask));
    });
}
int32_t mmd_bullet_add_joint(void* pointer, const MmdBulletJoint* d) noexcept {
    // Unsupported PMX extensions are reported, never silently converted to a spring joint.
    if (d && (d->type < 0 || d->type > 5)) { error("Unsupported PMX joint type."); return 2; }
    return guarded([&] {
        Context& c = context(pointer); if (!d) throw std::invalid_argument("Null joint description.");
        if (d->body_a == d->body_b || !finite(d->linear_lower) || !finite(d->linear_upper) || !finite(d->angular_lower)
            || !finite(d->angular_upper) || !finite(d->linear_spring) || !finite(d->angular_spring)) throw std::invalid_argument("Invalid joint description.");
        btRigidBody& a = *body(c, d->body_a).body; btRigidBody& b = *body(c, d->body_b).body;
        btTransform fa = transform(d->frame_a), fb = transform(d->frame_b);
        std::unique_ptr<btTypedConstraint> constraint;
        if (d->type == 0 || d->type == 1) {
            btGeneric6DofConstraint* dof;
            if (d->type == 0) {
                auto spring = new btGeneric6DofSpringConstraint(a, b, fa, fb, true); constraint.reset(spring); dof = spring;
                for (int axis = 0; axis < 6; axis++) {
                    const btScalar stiffness = axis < 3 ? vector(d->linear_spring)[axis] : vector(d->angular_spring)[axis - 3];
                    if (stiffness != 0) { spring->enableSpring(axis, true); spring->setStiffness(axis, stiffness); }
                }
                // The authored equilibrium is zero in the PMX joint frames, including reset at an animated pose.
            } else { dof = new btGeneric6DofConstraint(a, b, fa, fb, true); constraint.reset(dof); }
            dof->setLinearLowerLimit(vector(d->linear_lower)); dof->setLinearUpperLimit(vector(d->linear_upper));
            dof->setAngularLowerLimit(vector(d->angular_lower)); dof->setAngularUpperLimit(vector(d->angular_upper));
        } else if (d->type == 2) {
            constraint.reset(new btPoint2PointConstraint(a, b, fa.getOrigin(), fb.getOrigin()));
        } else if (d->type == 3) {
            auto cone = new btConeTwistConstraint(a, b, fa, fb); constraint.reset(cone);
            // PMX 2.1 field reuse follows nanoem's MIT physics_bullet.cc, not
            // the Spring6Dof interpretation of these same storage fields.
            cone->setLimit(d->angular_lower.z, d->angular_lower.y, d->angular_lower.x,
                d->linear_spring.x, d->linear_spring.y, d->linear_spring.z);
            cone->setDamping(d->linear_lower.x); cone->setFixThresh(d->linear_upper.x);
            cone->setMaxMotorImpulse(d->linear_upper.z);
            btQuaternion target; target.setEulerZYX(d->angular_spring.x, d->angular_spring.y, d->angular_spring.z);
            cone->setMotorTarget(target);
        } else if (d->type == 4) {
            auto slider = new btSliderConstraint(a, b, fa, fb, true); constraint.reset(slider);
            slider->setLowerLinLimit(d->linear_lower.x); slider->setUpperLinLimit(d->linear_upper.x);
            slider->setLowerAngLimit(d->angular_lower.x); slider->setUpperAngLimit(d->angular_upper.x);
            slider->setPoweredLinMotor(d->linear_spring.x > 0);
            slider->setTargetLinMotorVelocity(d->linear_spring.y); slider->setMaxLinMotorForce(d->linear_spring.z);
            slider->setPoweredAngMotor(d->angular_spring.x > 0);
            slider->setTargetAngMotorVelocity(d->angular_spring.y); slider->setMaxAngMotorForce(d->angular_spring.z);
        } else {
            auto hinge = new btHingeConstraint(a, b, fa, fb, true); constraint.reset(hinge);
            hinge->setLimit(d->angular_lower.x, d->angular_upper.x, d->linear_spring.x, d->linear_spring.y, d->linear_spring.z);
            hinge->enableAngularMotor(d->angular_spring.x > 0, d->angular_spring.y, d->angular_spring.z);
        }
        c.constraints.push_back(std::move(constraint)); c.world->addConstraint(c.constraints.back().get(), false);
    });
}
int32_t mmd_bullet_set_kinematic_target(void* pointer, int32_t index, const MmdBulletPose* target) noexcept {
    return guarded([&] {
        Body& value = body(context(pointer), index);
        if (!target) throw std::invalid_argument("Expected kinematic body and target pose.");
        set_kinematic_target_impl(value, *target);
    });
}
int32_t mmd_bullet_get_body_pose(void* pointer, int32_t index, MmdBulletPose* result) noexcept {
    return guarded([&] { if (!result) throw std::invalid_argument("Null pose result."); *result = pose(body(context(pointer), index).body->getWorldTransform()); });
}
int32_t mmd_bullet_step(void* pointer, float seconds) noexcept {
    return guarded([&] {
        if (!std::isfinite(seconds) || seconds <= 0 || seconds > 1.f / 15) throw std::invalid_argument("Invalid physics step.");
        // The managed session owns the fixed-step accumulator; there is no second native accumulator.
        Context& c=context(pointer);c.world->stepSimulation(seconds, 0);
        for(void* binding:c.softBindings)mmd_soft::postStep(binding);
        if(c.softWorld)c.softWorld->getWorldInfo().m_sparsesdf.GarbageCollect();
    });
}
int32_t mmd_bullet_apply_impulse(void* pointer, int32_t index, MmdBulletVector linear, MmdBulletVector angular) noexcept {
    return guarded([&] {
        Body& value = body(context(pointer), index);
        if (!finite(linear) || !finite(angular)) throw std::invalid_argument("Impulse momentum must be finite.");
        if (value.mode == 0 || value.temporary_kinematic || value.body->getInvMass() == 0) return;
        value.body->activate(true);
        value.body->applyCentralImpulse(vector(linear));
        value.body->applyTorqueImpulse(vector(angular));
    });
}
int32_t mmd_bullet_set_gravity(void* pointer, MmdBulletVector gravity) noexcept {
    return guarded([&] {
        Context& c = context(pointer);
        if (!finite(gravity)) throw std::invalid_argument("Gravity must be finite.");
        // Bullet 2.75's world setter only updates active bodies. Wake first so a
        // previously sleeping dynamic body receives the new acceleration too.
        for (auto& value : c.bodies)
            if (value && !value->body->isStaticOrKinematicObject()) value->body->activate(true);
        c.world->setGravity(vector(gravity));
        if(c.softWorld)c.softWorld->getWorldInfo().m_gravity=vector(gravity);
    });
}

int32_t mmd_bullet_add_body_slot(void* pointer, const MmdBulletBody* description, int32_t* result) noexcept {
    if (!result) { error("Null body slot result."); return 1; }
    *result = -1;
    int32_t index = -1;
    int32_t status = guarded([&] { index = static_cast<int32_t>(context(pointer).bodies.size()); });
    if (status != 0) return status;
    status = mmd_bullet_add_body(pointer, description); if (status == 0) *result = index;
    return status;
}
int32_t mmd_bullet_add_joint_slot(void* pointer, const MmdBulletJoint* description, int32_t* result) noexcept {
    if (!result) { error("Null joint slot result."); return 1; }
    *result = -1;
    int32_t index = -1;
    int32_t status = guarded([&] { index = static_cast<int32_t>(context(pointer).constraints.size()); });
    if (status != 0) return status;
    status = mmd_bullet_add_joint(pointer, description); if (status == 0) *result = index;
    return status;
}
int32_t mmd_bullet_remove_joint(void* pointer, int32_t index) noexcept {
    return guarded([&] {
        Context& c = context(pointer);
        if (index < 0 || static_cast<size_t>(index) >= c.constraints.size() || !c.constraints[index])
            throw std::out_of_range("Joint slot out of range or removed.");
        c.world->removeConstraint(c.constraints[index].get()); c.constraints[index].reset();
    });
}
int32_t mmd_bullet_remove_body(void* pointer, int32_t index) noexcept {
    return guarded([&] {
        Context& c = context(pointer); Body& value = body(c, index);
        for (auto& constraint : c.constraints)
            if (constraint && (&constraint->getRigidBodyA() == value.body.get() || &constraint->getRigidBodyB() == value.body.get()))
                throw std::invalid_argument("Remove referencing joints before removing a body.");
        for(void* binding:c.softBindings)if(mmd_soft::references(binding,value.body.get()))
            throw std::invalid_argument("Dispose referencing soft bindings before removing a body.");
        c.world->removeRigidBody(value.body.get()); c.bodies[index].reset();
    });
}
int32_t mmd_bullet_reset_body(void* pointer, int32_t index, const MmdBulletPose* target) noexcept {
    return guarded([&] {
        Context& c = context(pointer); Body& value = body(c, index);
        if (!target) throw std::invalid_argument("Null reset pose.");
        btTransform t = transform(*target);
        value.body->clearForces(); value.body->setLinearVelocity(btVector3(0, 0, 0)); value.body->setAngularVelocity(btVector3(0, 0, 0));
        value.body->setWorldTransform(t); value.body->setInterpolationWorldTransform(t); value.motion->setWorldTransform(t);
        if (value.body->getBroadphaseHandle())
            c.broadphase->getOverlappingPairCache()->cleanProxyFromPairs(value.body->getBroadphaseHandle(), c.dispatcher.get());
        c.world->updateSingleAabb(value.body.get()); value.body->activate(true);
    });
}
int32_t mmd_bullet_set_body_kinematic(void* pointer, int32_t index, int32_t enabled) noexcept {
    return guarded([&] {
        Context& c = context(pointer); Body& value = body(c, index);
        set_body_kinematic_impl(c, value, enabled);
    });
}

int32_t mmd_bullet_correct_joint_overextension(void* pointer, int32_t body_a, int32_t body_b, float maximum_distance) noexcept {
    return guarded([&] {
        correct_joint_overextension_impl(context(pointer), body_a, body_b, maximum_distance);
    });
}
int32_t mmd_bullet_features() noexcept {return 7;}
int32_t mmd_bullet_create_coupled(MmdBulletVector gravity,int32_t has_ground,float ground_y,void** result) noexcept {
    if(result)*result=nullptr;
    return guarded([&]{if(!result||!finite(gravity)||!std::isfinite(ground_y))throw std::invalid_argument("Invalid coupled world settings.");
        *result=new Context(gravity,has_ground!=0,ground_y,true);});
}
int32_t mmd_bullet_soft_create(void* pointer,const int32_t* slots,int32_t count,void** result) noexcept {
    if(result)*result=nullptr;
    return guarded([&]{Context& c=context(pointer);
        if(!c.softWorld)throw std::invalid_argument("This owner is not a coupled soft-rigid world.");
        if(!result||count<0||(count&&!slots))throw std::invalid_argument("Invalid rigid slot mapping.");
        std::vector<btRigidBody*> bodies;std::vector<int> modes;bodies.reserve(count);modes.reserve(count);
        for(int i=0;i<count;i++){Body& value=body(c,slots[i]);
            if(std::find(bodies.begin(),bodies.end(),value.body.get())!=bodies.end())throw std::invalid_argument("Duplicate rigid slot mapping.");
            bodies.push_back(value.body.get());modes.push_back(value.mode);}
        c.softBindings.reserve(c.softBindings.size()+1);
        void* binding=mmd_soft::createBorrowed(*c.softWorld,bodies,modes);c.softBindings.push_back(binding);*result=binding;
    });
}
int32_t mmd_bullet_soft_destroy(void* pointer,void* binding) noexcept {
    return guarded([&]{Context& c=context(pointer);auto found=std::find(c.softBindings.begin(),c.softBindings.end(),binding);
        if(found==c.softBindings.end())throw std::invalid_argument("Soft binding is stale or belongs to another owner.");
        mmd_soft::destroyBorrowed(binding);c.softBindings.erase(found);
    });
}
int32_t mmd_bullet_get_body_velocity(void* pointer,int32_t index,MmdBulletVector* linear,MmdBulletVector* angular) noexcept {
    return guarded([&]{auto& value=body(context(pointer),index);if(!linear||!angular)throw std::invalid_argument("Null velocity output.");
        const auto& l=value.body->getLinearVelocity();const auto& a=value.body->getAngularVelocity();
        *linear={l.x(),l.y(),l.z()};*angular={a.x(),a.y(),a.z()};});
}
int32_t mmd_bullet_set_kinematic_targets_batch(void* pointer, const MmdBulletKinematicTargetItem* items, int32_t count) noexcept {
    return guarded([&] {
        Context& c = context(pointer);
        if (count < 0 || (count > 0 && !items)) throw std::invalid_argument("Invalid kinematic target batch.");
        for (int32_t i = 0; i < count; ++i) {
            const MmdBulletKinematicTargetItem& item = items[i];
            Body& value = body(c, item.body_index);
            set_body_kinematic_impl(c, value, item.is_kinematic);
            if (item.is_kinematic != 0) set_kinematic_target_impl(value, item.target);
        }
    });
}
int32_t mmd_bullet_correct_joints_and_get_poses(void* pointer, const MmdBulletJointPullItem* joints, int32_t joint_count, const int32_t* body_indices, int32_t body_count, MmdBulletPose* poses_out) noexcept {
    return guarded([&] {
        Context& c = context(pointer);
        if (joint_count < 0 || (joint_count > 0 && !joints) || body_count < 0 || (body_count > 0 && (!body_indices || !poses_out)))
            throw std::invalid_argument("Invalid joint pull or pose batch.");
        for (int32_t i = 0; i < joint_count; ++i)
            correct_joint_overextension_impl(c, joints[i].body_a, joints[i].body_b, joints[i].maximum_distance);
        for (int32_t i = 0; i < body_count; ++i)
            poses_out[i] = pose(body(c, body_indices[i]).body->getWorldTransform());
    });
}
