// Project pair-specific adapter for Bullet 2.75. The solver remains upstream Bullet.
// See LICENSE.Bullet.txt and artifacts/stage-20261021-core-completion/cluster-ground-design.md.
#pragma once
#include <BulletSoftBody/btSoftBody.h>
#include <BulletSoftBody/btSoftRigidCollisionAlgorithm.h>
#include <BulletSoftBody/btSoftSoftCollisionAlgorithm.h>
#include <BulletSoftBody/btSoftBodyRigidBodyCollisionConfiguration.h>
#include <cmath>
#include <initializer_list>
#include <stdexcept>

namespace mmd_soft {
enum Feature { KinematicNear = 1, ClusterPlane = 2, SharedNodes = 4, SharedClusters = 16 };

// Reuse the existing algorithm for disabled-object handling, allocator footprint
// and ordinary pairs. The temporary route applies only while generating plane
// node contacts, before any cluster or position solver runs.
class PlaneSoftCollisionAlgorithm final : public btSoftRigidCollisionAlgorithm {
public:
 PlaneSoftCollisionAlgorithm(const btCollisionAlgorithmConstructionInfo& ci,
     btCollisionObject* a, btCollisionObject* b, bool swapped)
     : btSoftRigidCollisionAlgorithm(nullptr, ci, a, b, swapped) {}

 void processCollision(btCollisionObject* a, btCollisionObject* b,
     const btDispatcherInfo& info, btManifoldResult* result) override {
  btSoftBody* soft = btSoftBody::upcast(a);
  btCollisionObject* rigid = b;
  if (!soft) { soft = btSoftBody::upcast(b); rigid = a; }
  if (soft && rigid->getCollisionShape()->getShapeType() == STATIC_PLANE_PROXYTYPE) {
   struct Restore {
    btSoftBody& body; int flags;
    ~Restore() { body.m_cfg.collisions = flags; }
   } restore{*soft, soft->m_cfg.collisions};
   soft->m_cfg.collisions = (restore.flags & ~btSoftBody::fCollision::RVSmask)
       | btSoftBody::fCollision::SDF_RS;
   btSoftRigidCollisionAlgorithm::processCollision(a, b, info, result);
  } else {
   btSoftRigidCollisionAlgorithm::processCollision(a, b, info, result);
  }
 }

 struct CreateFunc final : btCollisionAlgorithmCreateFunc {
  btCollisionAlgorithm* CreateCollisionAlgorithm(btCollisionAlgorithmConstructionInfo& ci,
      btCollisionObject* a, btCollisionObject* b) override {
   void* memory = ci.m_dispatcher1->allocateCollisionAlgorithm(sizeof(PlaneSoftCollisionAlgorithm));
   return new(memory) PlaneSoftCollisionAlgorithm(ci, a, b, m_swapped);
  }
 };
};
static_assert(sizeof(PlaneSoftCollisionAlgorithm) == sizeof(btSoftRigidCollisionAlgorithm),
    "Plane adapter must fit the upstream collision algorithm pool");

// Bullet 2.75 VF contacts divide the closest-point vector by its length. Exact
// coincident surfaces have no such direction (0/0). Retain the contact and its
// mass/friction terms, selecting a geometric face normal only for this undefined
// case. Opposite node/face passes use opposite pair directions so they separate
// the two surfaces consistently instead of cancelling each other.
class FiniteSoftSoftCollisionAlgorithm final : public btSoftSoftCollisionAlgorithm {
 static bool finite(const btVector3& n) { return std::isfinite(n.x())&&std::isfinite(n.y())&&std::isfinite(n.z()); }
 static btVector3 faceNormal(const btSoftBody::Face& f) {
  return (f.m_n[1]->m_x-f.m_n[0]->m_x).cross(f.m_n[2]->m_x-f.m_n[0]->m_x);
 }
 // A contact whose normal cannot be repaired is dropped instead of throwing: an exception here unwinds out of
 // collision dispatch after the soft bodies were already predicted, leaving the world half-stepped. Dropping a
 // degenerate contact only loses one undefined push for this step; the next step sees the pair again.
 static void repair(btSoftBody& owner,int first,const btVector3& direction) {
  for(int i=first;i<owner.m_scontacts.size();){
   auto& contact=owner.m_scontacts[i];if(finite(contact.m_normal)){i++;continue;}
   btVector3 normal=direction.length2()>0?faceNormal(*contact.m_face):btVector3(0,0,0);
   if(!finite(normal)||normal.length2()==0){
    owner.m_scontacts[i]=owner.m_scontacts[owner.m_scontacts.size()-1];owner.m_scontacts.pop_back();continue;
   }
   normal.normalize();if(normal.dot(direction)<0)normal=-normal;contact.m_normal=normal;i++;
  }
 }
public:
 using btSoftSoftCollisionAlgorithm::btSoftSoftCollisionAlgorithm;
 void processCollision(btCollisionObject* a,btCollisionObject* b,
     const btDispatcherInfo& info,btManifoldResult* result) override {
  auto& first=*btSoftBody::upcast(a);auto& second=*btSoftBody::upcast(b);
  const int startFirst=first.m_scontacts.size(),startSecond=second.m_scontacts.size();
  btSoftSoftCollisionAlgorithm::processCollision(a,b,info,result);
  bool needsRepair=false;
  for(int i=startFirst;i<first.m_scontacts.size();i++)needsRepair|=!finite(first.m_scontacts[i].m_normal);
  for(int i=startSecond;i<second.m_scontacts.size();i++)needsRepair|=!finite(second.m_scontacts[i].m_normal);
  if(!needsRepair)return;
  btVector3 direction(0,0,0);
  for(auto* body:{&first,&second}){
   for(int i=0;i<body->m_faces.size();i++){
    btVector3 candidate=faceNormal(body->m_faces[i]);
    if(finite(candidate)&&candidate.length2()>0){direction=candidate.normalized();break;}
   }
   if(direction.length2()>0)break;
  }
  // With no nondegenerate reference face every undefined contact of this pair is dropped (see repair).
  repair(first,startFirst,direction);repair(second,startSecond,-direction);
 }
 struct CreateFunc final : btCollisionAlgorithmCreateFunc {
  btCollisionAlgorithm* CreateCollisionAlgorithm(btCollisionAlgorithmConstructionInfo& ci,
      btCollisionObject* a,btCollisionObject* b) override {
   void* memory=ci.m_dispatcher1->allocateCollisionAlgorithm(sizeof(FiniteSoftSoftCollisionAlgorithm));
   return new(memory) FiniteSoftSoftCollisionAlgorithm(nullptr,ci,a,b);
  }
 };
};
static_assert(sizeof(FiniteSoftSoftCollisionAlgorithm)==sizeof(btSoftSoftCollisionAlgorithm),
    "Coincident adapter must fit the upstream collision algorithm pool");

class PlaneSoftConfiguration final : public btSoftBodyRigidBodyCollisionConfiguration {
 PlaneSoftCollisionAlgorithm::CreateFunc direct, swapped;
 FiniteSoftSoftCollisionAlgorithm::CreateFunc softSoft;
public:
 PlaneSoftConfiguration() { swapped.m_swapped = true; }
 btCollisionAlgorithmCreateFunc* getCollisionAlgorithmCreateFunc(int a, int b) override {
  if (a == SOFTBODY_SHAPE_PROXYTYPE && b == STATIC_PLANE_PROXYTYPE) return &direct;
  if (a == STATIC_PLANE_PROXYTYPE && b == SOFTBODY_SHAPE_PROXYTYPE) return &swapped;
  if (a == SOFTBODY_SHAPE_PROXYTYPE && b == SOFTBODY_SHAPE_PROXYTYPE) return &softSoft;
  return btSoftBodyRigidBodyCollisionConfiguration::getCollisionAlgorithmCreateFunc(a, b);
 }
};
}
