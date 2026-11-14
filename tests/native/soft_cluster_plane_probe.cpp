// Standalone native contract probe. Root builds this with the same Bullet sources
// and cache-only analytic SDF patch used by scripts/build-bullet-soft.py.
#include <btBulletDynamicsCommon.h>
#include <BulletSoftBody/btSoftRigidDynamicsWorld.h>
#include "mmd_soft_plane_collision.h"
#include <cstdio>
#include <stdexcept>

static void require(bool value, const char* message) {
 if (!value) throw std::runtime_error(message);
}

int main() {
 try {
  mmd_soft::PlaneSoftConfiguration config;
  btCollisionDispatcher dispatcher(&config);
  btDbvtBroadphase broad;
  btSequentialImpulseConstraintSolver solver;
  btSoftRigidDynamicsWorld world(&dispatcher, &broad, &solver, &config);
  world.setGravity(btVector3(0,0,0));
  auto& info = world.getWorldInfo();
  info.m_gravity.setZero(); info.m_broadphase = &broad; info.m_dispatcher = &dispatcher;
  info.air_density = 0; info.water_density = 0; info.water_offset = 0;
  info.water_normal.setZero(); info.m_sparsesdf.Initialize();
  btStaticPlaneShape planeShape(btVector3(0,1,0), 0);
  btRigidBody::btRigidBodyConstructionInfo planeInfo(0, nullptr, &planeShape);
  btRigidBody plane(planeInfo);
  world.addRigidBody(&plane);
  const btVector3 points[] = {btVector3(-.3f,.01f,0), btVector3(.3f,.01f,0),
      btVector3(0,.01f,.6f), btVector3(0,.3f,.2f)};
  btSoftBody soft(&info, 4, points, nullptr);
  for (int i=0; i<4; ++i) for(int j=i+1; j<4; ++j) soft.appendLink(i,j);
  soft.appendFace(0,1,2); soft.appendFace(0,3,1); soft.appendFace(0,2,3); soft.appendFace(1,3,2);
  soft.setTotalMass(1); soft.getCollisionShape()->setMargin(.02f);
  soft.generateClusters(1);
  const int flags = btSoftBody::fCollision::CL_RS | btSoftBody::fCollision::CL_SS;
  soft.m_cfg.collisions = flags;
  world.addSoftBody(&soft);
  soft.predictMotion(1.f/120);
  world.performDiscreteCollisionDetection();
  const int planeContacts = soft.m_rcontacts.size();
  require(planeContacts > 0, "plane did not generate native node contacts");
  require(soft.m_clusters.size() > 0, "clusters were not generated");
  require(soft.m_cfg.collisions == flags, "plane dispatch changed cluster flags");
  require(soft.m_joints.size() == 0, "plane entered convex cluster contact route");
  btVector3 low, high; planeShape.getAabb(btTransform::getIdentity(), low, high);
  // Verify Bullet's actual infinite-shape sentinel. Float32 Bullet 2.75 uses
  // 1e18f (so squaring remains finite), not the double build's 1e30 value.
  const btVector3 infinite(btScalar(BT_LARGE_FLOAT),btScalar(BT_LARGE_FLOAT),btScalar(BT_LARGE_FLOAT));
  require(planeShape.getShapeType() == STATIC_PLANE_PROXYTYPE && low == -infinite && high == infinite,
      "ground is not a true infinite static plane");
  btCollisionAlgorithmConstructionInfo pairInfo;
  pairInfo.m_dispatcher1 = &dispatcher;
  mmd_soft::PlaneSoftCollisionAlgorithm direct(pairInfo, &soft, &plane, false);
  mmd_soft::PlaneSoftCollisionAlgorithm swapped(pairInfo, &plane, &soft, true);
  soft.m_rcontacts.clear();
  direct.processCollision(&soft, &plane, world.getDispatchInfo(), nullptr);
  require(soft.m_rcontacts.size() == planeContacts && soft.m_cfg.collisions == flags,
      "direct plane dispatch differs or leaks flags");
  soft.m_rcontacts.clear();
  swapped.processCollision(&plane, &soft, world.getDispatchInfo(), nullptr);
  require(soft.m_rcontacts.size() == planeContacts && soft.m_cfg.collisions == flags,
      "swapped plane dispatch differs or leaks flags");
  soft.m_rcontacts.clear(); soft.m_collisionDisabledObjects.push_back(&plane);
  world.performDiscreteCollisionDetection();
  require(soft.m_rcontacts.size() == 0, "plane ignored disabled-object filter");
  soft.m_collisionDisabledObjects.clear();
  world.removeSoftBody(&soft); world.addSoftBody(&soft, 1, 0);
  world.performDiscreteCollisionDetection();
  require(soft.m_rcontacts.size() == 0, "plane ignored broadphase collision mask");
  world.removeSoftBody(&soft); world.addSoftBody(&soft);
  btSphereShape sphereShape(.5f);
  btRigidBody::btRigidBodyConstructionInfo sphereInfo(0, nullptr, &sphereShape);
  btRigidBody sphere(sphereInfo);
  sphere.setWorldTransform(btTransform(btQuaternion::getIdentity(), btVector3(0,.1f,.2f)));
  sphere.setInterpolationWorldTransform(sphere.getWorldTransform());
  world.addRigidBody(&sphere);
  world.performDiscreteCollisionDetection();
  require(soft.m_joints.size() > 0, "convex pair did not generate actual cluster CJoint");
  require(soft.m_cfg.collisions == flags, "convex dispatch lost CL_RS or CL_SS");
  const int clusterContacts = soft.m_joints.size();
  std::printf("{\"planeNodeContacts\":%d,\"clusters\":%d,\"convexClusterContacts\":%d,\"collisionFlags\":%d,\"infinitePlane\":true,\"directAndSwapped\":true,\"disabledFilter\":true,\"maskFilter\":true}\n",
      planeContacts, soft.m_clusters.size(), clusterContacts, soft.m_cfg.collisions);
  world.removeSoftBody(&soft); world.removeRigidBody(&sphere); world.removeRigidBody(&plane);
  info.m_sparsesdf.Reset();
  return 0;
 } catch (const std::exception& error) {
  std::fprintf(stderr, "%s\n", error.what()); return 1;
 }
}
