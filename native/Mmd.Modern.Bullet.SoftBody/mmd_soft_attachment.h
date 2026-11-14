// Internal same-DLL bridge. Bullet pointers never cross a DLL or managed API boundary.
#pragma once
#include <BulletSoftBody/btSoftRigidDynamicsWorld.h>
#include <vector>
namespace mmd_soft {
// Suspended soft content stays in the collision world. Only its contact
// constraints participate; animation anchors and numerical integration stop.
class CoupledDynamicsWorld final : public btSoftRigidDynamicsWorld {
    btSoftBodyArray suspended;
protected:
    void predictUnconstraintMotion(btScalar seconds) override;
    void internalSingleStepSimulation(btScalar seconds) override;
public:
    using btSoftRigidDynamicsWorld::btSoftRigidDynamicsWorld;
    void reserveSuspended(int count) { suspended.reserve(suspended.size()+count); }
    void suspend(btSoftBody* body) { suspended.push_back(body); getSoftBodyArray().remove(body); }
    void forgetSuspended(btSoftBody* body) { suspended.remove(body); }
};
void* createBorrowed(btSoftRigidDynamicsWorld& world, const std::vector<btRigidBody*>& bodies,
                     const std::vector<int>& authoredModes);
void destroyBorrowed(void* binding) noexcept;
bool references(void* binding, btRigidBody* body);
void postStep(void* binding);
}
