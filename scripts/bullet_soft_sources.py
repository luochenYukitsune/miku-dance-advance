"""Cache-only compatibility preparation shared by standalone and coupled builds."""
from pathlib import Path
import hashlib
import os
import shutil


def _replace_once(data, old, new, label):
    assert data.count(old) == 1, f'{label}: expected 1 occurrence of {old!r}, got {data.count(old)}'
    return data.replace(old, new)


def _rel_or_abs(target, base_dir):
    try:
        return Path(os.path.relpath(target.resolve(), base_dir.resolve())).as_posix()
    except ValueError:
        return target.resolve().as_posix()


def _copy_headers(src_dir, dst_dir):
    dst_dir.mkdir(parents=True, exist_ok=True)
    for header_file in src_dir.glob('*.h'):
        (dst_dir / header_file.name).write_bytes(header_file.read_bytes())


def prepare_soft_sources(n, output_directory):
    out = Path(output_directory) / 'unused'
    # Modern Clang rejects the old uninitialized const aggregate template. Preserve
    # vendored bytes and compile a documented cache-only value-initialized copy.
    compat=out.parent/'compat'
    if compat.exists():
        shutil.rmtree(compat)
    compat.mkdir(exist_ok=True)
    compat_upstream=compat/'upstream';compat_soft=compat_upstream/'BulletSoftBody';compat_soft.mkdir(parents=True,exist_ok=True)
    for original_file in (n/'upstream/BulletSoftBody').iterdir():
        if original_file.is_file():(compat_soft/original_file.name).write_bytes(original_file.read_bytes())
    header=n/'upstream/BulletSoftBody/btSoftBodyInternals.h';original=header.read_bytes()
    before=b'static const T\tzerodummy;';after=b'static const T\tzerodummy = {};'
    assert original.count(before)==1
    (compat_soft/header.name).write_bytes(original.replace(before,after))
    # Bullet 2.75 SDF returns constant zero for non-convex shapes. A static plane
    # therefore cannot produce its actual signed distance or normal. Supply the
    # analytic plane distance in the cache copy; retain all original upstream bytes.
    sdf=n/'upstream/BulletSoftBody/btSparseSDF.h';sdf_original=sdf.read_bytes()
    sdf_before=b'if(shape->isConvex())'
    sdf_after=b'''if(shape->getShapeType()==STATIC_PLANE_PROXYTYPE)
        {
            const btStaticPlaneShape* plane=static_cast<const btStaticPlaneShape*>(shape);
            return plane->getPlaneNormal().dot(x)-plane->getPlaneConstant();
        }
        if(shape->isConvex())'''
    assert sdf_original.count(sdf_before)==1
    (compat_soft/sdf.name).write_bytes(b'#include <BulletCollision/CollisionShapes/btStaticPlaneShape.h>\n'+sdf_original.replace(sdf_before,sdf_after))
    # Cluster seeding formerly multiplied two signed ints before the modulo.
    # Node 71888 crosses INT_MAX even with only five clusters. Widen just this
    # integer expression; all previously defined seed assignments stay identical.
    soft_body=n/'upstream/BulletSoftBody/btSoftBody.cpp';soft_body_original=soft_body.read_bytes()
    seed_before=b'm_clusters[(i*29873)%m_clusters.size()]'
    seed_after=b'm_clusters[static_cast<int>((static_cast<long long>(i)*29873)%m_clusters.size())]'
    assert soft_body_original.count(seed_before)==1
    compiled_soft_body=compat_soft/soft_body.name
    compiled_soft_body.write_bytes(b'// ALTERED VERSION: widen cluster seed indexing before modulo to avoid int32 overflow.\n'
        +soft_body_original.replace(seed_before,seed_after))

    vendor_src = n.parents[1] / 'third_party/bullet-2.75/src'

    # 1. Self-contained rigid-only optimization headers inside compat_upstream.
    (compat_upstream / 'mmd_bullet_ground_bounds.h').write_text(
        '// Generated cache-only header for rigid-only horizontal ground bounds optimization.\n'
        '#pragma once\n'
        '#include "BulletCollision/CollisionDispatch/btCollisionObject.h"\n'
        '#include "BulletCollision/CollisionShapes/btConvexShape.h"\n'
        '#include "BulletCollision/NarrowPhaseCollision/btPersistentManifold.h"\n'
        '#include <utility>\n\n'
        'namespace mmd_bullet_parallel {\n'
        'inline char horizontalGround = 0;\n\n'
        'template<class Shape>\n'
        'class ImmutableShape final : public Shape {\n'
        '    const btScalar disc;\n'
        'public:\n'
        '    template<class... Args>\n'
        '    explicit ImmutableShape(Args&&... args)\n'
        '        : Shape(std::forward<Args>(args)...), disc(Shape::getAngularMotionDisc()) {}\n'
        '    btScalar getAngularMotionDisc() const override { return disc; }\n'
        '};\n\n'
        'inline bool aboveGround(const btCollisionObject& convex, const btCollisionObject& ground,\n'
        '                        const btConvexShape& shape, const btPersistentManifold& manifold) {\n'
        '    const double height = double(convex.getWorldTransform().getOrigin().y())\n'
        '                        - double(ground.getWorldTransform().getOrigin().y());\n'
        '    return height > 2.0 * shape.getAngularMotionDisc() + manifold.getContactBreakingThreshold();\n'
        '}\n'
        '}\n',
        encoding='utf-8')

    (compat_upstream / 'mmd_bullet_pair_query.h').write_text(
        '// Generated cache-only header for rigid-only parallel narrowphase queries.\n'
        '#pragma once\n'
        '#include "BulletCollision/CollisionDispatch/btConvexConvexAlgorithm.h"\n'
        '#include "BulletCollision/CollisionDispatch/btManifoldResult.h"\n'
        '#include "BulletCollision/NarrowPhaseCollision/btDiscreteCollisionDetectorInterface.h"\n'
        '#include "BulletCollision/NarrowPhaseCollision/btConvexPenetrationDepthSolver.h"\n'
        '#include "BulletCollision/NarrowPhaseCollision/btVoronoiSimplexSolver.h"\n\n'
        'namespace mmd_bullet_parallel {\n'
        'struct Query final : btDiscreteCollisionDetectorInterface::Result {\n'
        '    int ordinal;\n'
        '    btCollisionObject* a;\n'
        '    btCollisionObject* b;\n'
        '    btScalar threshold;\n'
        '    btVector3 normal{0, 0, 0}, point{0, 0, 0};\n'
        '    btScalar depth{};\n'
        '    bool hit{};\n'
        '    bool hasWitnesses{};\n'
        '    btVector3 witnessA{0, 0, 0}, witnessB{0, 0, 0}, closest{0, 0, 0};\n'
        '    Query(int ordinal, btCollisionObject* a, btCollisionObject* b, btScalar threshold)\n'
        '        : ordinal(ordinal), a(a), b(b), threshold(threshold) {}\n'
        '    void setShapeIdentifiersA(int, int) override {}\n'
        '    void setShapeIdentifiersB(int, int) override {}\n'
        '    void addContactPoint(const btVector3& n, const btVector3& p, btScalar d) override {\n'
        '        normal = n; point = p; depth = d; hit = true;\n'
        '    }\n'
        '    void compute(btVoronoiSimplexSolver&, btConvexPenetrationDepthSolver&);\n'
        '};\n\n'
        'struct PreparedResult final : btManifoldResult {\n'
        '    const Query& query;\n'
        '    explicit PreparedResult(const Query& q) : btManifoldResult(q.a, q.b), query(q) {}\n'
        '    void emit() {\n'
        '        if (query.hit) addContactPoint(query.normal, query.point, query.depth);\n'
        '    }\n'
        '    bool replayGjk(btVoronoiSimplexSolver& serial) {\n'
        '        if (!query.hasWitnesses) return false;\n'
        '        serial.m_cachedP1 = query.witnessA;\n'
        '        serial.m_cachedP2 = query.witnessB;\n'
        '        serial.m_cachedV = query.closest;\n'
        '        emit();\n'
        '        return true;\n'
        '    }\n'
        '};\n'
        '}\n',
        encoding='utf-8')

    # 2. Thread-local Bullet 2.75 global counters to eliminate cross-thread races and false sharing.
    # LinearMath/btAlignedAllocator.cpp
    alloc_src = vendor_src / 'LinearMath/btAlignedAllocator.cpp'
    alloc_dst = compat_upstream / 'LinearMath/btAlignedAllocator.cpp'
    _copy_headers(alloc_src.parent, alloc_dst.parent)
    alloc_data = alloc_src.read_bytes()
    alloc_data = _replace_once(alloc_data, b'int gNumAlignedAllocs = 0;', b'thread_local int gNumAlignedAllocs = 0;', 'btAlignedAllocator.cpp')
    alloc_data = _replace_once(alloc_data, b'int gNumAlignedFree = 0;', b'thread_local int gNumAlignedFree = 0;', 'btAlignedAllocator.cpp')
    alloc_data = _replace_once(alloc_data, b'int gTotalBytesAlignedAllocs = 0;', b'thread_local int gTotalBytesAlignedAllocs = 0;', 'btAlignedAllocator.cpp')
    alloc_dst.write_bytes(b'// ALTERED VERSION: thread_local allocation counters for concurrent Bullet worlds.\n' + alloc_data)

    # BulletCollision/BroadphaseCollision headers and sources
    broad_src_dir = vendor_src / 'BulletCollision/BroadphaseCollision'
    broad_dst_dir = compat_upstream / 'BulletCollision/BroadphaseCollision'
    _copy_headers(broad_src_dir, broad_dst_dir)

    pair_hdr = broad_dst_dir / 'btOverlappingPairCache.h'
    pair_hdr_data = pair_hdr.read_bytes()
    for sym in (b'gRemovePairs', b'gAddedPairs', b'gFindPairs'):
        pair_hdr_data = _replace_once(pair_hdr_data, b'extern int ' + sym + b';', b'extern thread_local int ' + sym + b';', 'btOverlappingPairCache.h')
    pair_hdr.write_bytes(b'// ALTERED VERSION: thread_local pair cache counters.\n' + pair_hdr_data)

    axis_hdr = broad_dst_dir / 'btAxisSweep3.h'
    axis_hdr_data = _replace_once(axis_hdr.read_bytes(), b'extern int gOverlappingPairs;', b'extern thread_local int gOverlappingPairs;', 'btAxisSweep3.h')
    axis_hdr.write_bytes(b'// ALTERED VERSION: thread_local overlapping pair counter.\n' + axis_hdr_data)

    pair_cpp_data = (broad_src_dir / 'btOverlappingPairCache.cpp').read_bytes()
    pair_cpp_data = _replace_once(pair_cpp_data, b'int\tgOverlappingPairs = 0;', b'thread_local int\tgOverlappingPairs = 0;', 'btOverlappingPairCache.cpp')
    pair_cpp_data = _replace_once(pair_cpp_data, b'int gRemovePairs =0;', b'thread_local int gRemovePairs =0;', 'btOverlappingPairCache.cpp')
    pair_cpp_data = _replace_once(pair_cpp_data, b'int gAddedPairs =0;', b'thread_local int gAddedPairs =0;', 'btOverlappingPairCache.cpp')
    pair_cpp_data = _replace_once(pair_cpp_data, b'int gFindPairs =0;', b'thread_local int gFindPairs =0;', 'btOverlappingPairCache.cpp')
    (broad_dst_dir / 'btOverlappingPairCache.cpp').write_bytes(b'// ALTERED VERSION: thread_local pair cache counters.\n' + pair_cpp_data)

    for broad_name in ('btSimpleBroadphase.cpp', 'btMultiSapBroadphase.cpp'):
        b_data = _replace_once((broad_src_dir / broad_name).read_bytes(), b'extern int gOverlappingPairs;', b'extern thread_local int gOverlappingPairs;', broad_name)
        (broad_dst_dir / broad_name).write_bytes(b'// ALTERED VERSION: thread_local overlapping pair counter.\n' + b_data)

    (broad_dst_dir / 'btAxisSweep3.cpp').write_bytes((broad_src_dir / 'btAxisSweep3.cpp').read_bytes())

    # Ensure every translation unit that transitively includes btOverlappingPairCache.h or btAxisSweep3.h
    # sees the compat_upstream thread_local declarations first even when -I vendor_src precedes -I compat_upstream.
    axis_compat_hdr = broad_dst_dir / 'btAxisSweep3.h'
    soft_world_cpp = compat_soft / 'btSoftRigidDynamicsWorld.cpp'
    soft_world_cpp.write_bytes(
        (f'// ALTERED VERSION: include thread_local broadphase counter headers first.\n'
         f'#include "{_rel_or_abs(axis_compat_hdr, soft_world_cpp.parent)}"\n').encode('utf-8')
        + soft_world_cpp.read_bytes()
    )

    wrap_targets = [
        vendor_src / 'BulletCollision/BroadphaseCollision/btDbvtBroadphase.cpp',
        vendor_src / 'BulletCollision/CollisionDispatch/btCollisionWorld.cpp',
        vendor_src / 'BulletCollision/CollisionDispatch/btGhostObject.cpp',
        vendor_src / 'BulletCollision/CollisionDispatch/btSimulationIslandManager.cpp',
        vendor_src / 'BulletCollision/Gimpact/btGImpactCollisionAlgorithm.cpp',
        vendor_src / 'BulletCollision/Gimpact/btGImpactShape.cpp',
        vendor_src / 'BulletDynamics/Character/btKinematicCharacterController.cpp',
        vendor_src / 'BulletDynamics/Dynamics/btContinuousDynamicsWorld.cpp',
        vendor_src / 'BulletDynamics/Dynamics/btSimpleDynamicsWorld.cpp',
        vendor_src / 'BulletDynamics/Vehicle/btRaycastVehicle.cpp',
        n / 'mmd_bullet_soft.cpp',
    ]
    for orig in wrap_targets:
        if orig == n / 'mmd_bullet_soft.cpp':
            dst = compat_upstream / orig.name
        else:
            dst = compat_upstream / orig.relative_to(vendor_src)
        dst.parent.mkdir(parents=True, exist_ok=True)
        axis_inc = _rel_or_abs(axis_compat_hdr, dst.parent)
        orig_inc = _rel_or_abs(orig, dst.parent)
        dst.write_text(
            f'// ALTERED VERSION: include thread_local broadphase counter headers first.\n'
            f'#include "{axis_inc}"\n'
            f'#include "{orig_inc}"\n',
            encoding='utf-8')

    # BulletCollision/CollisionDispatch: headers, dispatcher TLS, ground bounds, convex-convex parallel
    disp_src_dir = vendor_src / 'BulletCollision/CollisionDispatch'
    disp_dst_dir = compat_upstream / 'BulletCollision/CollisionDispatch'
    _copy_headers(disp_src_dir, disp_dst_dir)

    disp_cpp = _replace_once((disp_src_dir / 'btCollisionDispatcher.cpp').read_bytes(),
                             b'int gNumManifold = 0;', b'thread_local int gNumManifold = 0;', 'btCollisionDispatcher.cpp')
    (disp_dst_dir / 'btCollisionDispatcher.cpp').write_bytes(
        b'// ALTERED VERSION: thread_local manifold counter.\n'
        b'#include "../BroadphaseCollision/btAxisSweep3.h"\n'
        + disp_cpp)

    plane_cpp = (disp_src_dir / 'btConvexPlaneCollisionAlgorithm.cpp').read_bytes()
    plane_marker = b'//first perform a collision query'
    plane_inject = (
        b'#ifdef MMD_BULLET_RIGID_PARALLEL\n'
        b'\tif (planeObj->getUserPointer() == &mmd_bullet_parallel::horizontalGround &&\n'
        b'\t\tm_manifoldPtr->getNumContacts() == 0 &&\n'
        b'\t\tmmd_bullet_parallel::aboveGround(*convexObj, *planeObj, *convexShape, *m_manifoldPtr))\n'
        b'\t{\n'
        b'\t\tresultOut->setPersistentManifold(m_manifoldPtr);\n'
        b'\t\treturn;\n'
        b'\t}\n'
        b'#endif\n\n\t//first perform a collision query'
    )
    plane_cpp = _replace_once(plane_cpp, plane_marker, plane_inject, 'btConvexPlaneCollisionAlgorithm.cpp')
    (disp_dst_dir / 'btConvexPlaneCollisionAlgorithm.cpp').write_bytes(
        b'// ALTERED VERSION: rigid-only horizontal ground early-out.\n'
        b'#ifdef MMD_BULLET_RIGID_PARALLEL\n'
        b'#include "../../mmd_bullet_ground_bounds.h"\n'
        b'#endif\n'
        + plane_cpp)

    convex_cpp = (disp_src_dir / 'btConvexConvexAlgorithm.cpp').read_bytes()
    convex_create = b'btConvexConvexAlgorithm::CreateFunc::CreateFunc'
    convex_query_def = (
        b'#ifdef MMD_BULLET_RIGID_PARALLEL\n'
        b'#include "../../mmd_bullet_pair_query.h"\n'
        b'void mmd_bullet_parallel::Query::compute(btVoronoiSimplexSolver& simplex, btConvexPenetrationDepthSolver& penetration) {\n'
        b'    auto* shapeA = static_cast<const btConvexShape*>(a->getCollisionShape());\n'
        b'    auto* shapeB = static_cast<const btConvexShape*>(b->getCollisionShape());\n'
        b'#ifndef BT_DISABLE_CAPSULE_CAPSULE_COLLIDER\n'
        b'    if (shapeA->getShapeType() == CAPSULE_SHAPE_PROXYTYPE && shapeB->getShapeType() == CAPSULE_SHAPE_PROXYTYPE) {\n'
        b'        auto* ca = static_cast<const btCapsuleShape*>(shapeA);\n'
        b'        auto* cb = static_cast<const btCapsuleShape*>(shapeB);\n'
        b'        depth = capsuleCapsuleDistance(normal, point, ca->getHalfHeight(), ca->getRadius(),\n'
        b'            cb->getHalfHeight(), cb->getRadius(), ca->getUpAxis(), cb->getUpAxis(),\n'
        b'            a->getWorldTransform(), b->getWorldTransform(), threshold);\n'
        b'        hit = depth < threshold;\n'
        b'        return;\n'
        b'    }\n'
        b'#endif\n'
        b'    btGjkPairDetector detector(shapeA, shapeB, &simplex, &penetration);\n'
        b'    btGjkPairDetector::ClosestPointInput input;\n'
        b'    input.m_maximumDistanceSquared = shapeA->getMargin() + shapeB->getMargin() + threshold;\n'
        b'    input.m_maximumDistanceSquared *= input.m_maximumDistanceSquared;\n'
        b'    input.m_transformA = a->getWorldTransform();\n'
        b'    input.m_transformB = b->getWorldTransform();\n'
        b'    detector.getClosestPoints(input, *this, nullptr);\n'
        b'    hasWitnesses = !simplex.emptySimplex();\n'
        b'    if (hasWitnesses) {\n'
        b'        witnessA = simplex.m_cachedP1;\n'
        b'        witnessB = simplex.m_cachedP2;\n'
        b'        closest = simplex.m_cachedV;\n'
        b'    }\n'
        b'}\n'
        b'#endif\n\n'
        + convex_create
    )
    convex_cpp = _replace_once(convex_cpp, convex_create, convex_query_def, 'btConvexConvexAlgorithm.cpp')
    cap_marker = b'btCapsuleShape* capsuleA = '
    cap_inject = (
        b'#ifdef MMD_BULLET_RIGID_PARALLEL\n'
        b'\t\tif (auto* ready = dynamic_cast<mmd_bullet_parallel::PreparedResult*>(resultOut)) {\n'
        b'\t\t\tready->emit();\n'
        b'\t\t\tresultOut->refreshContactPoints();\n'
        b'\t\t\treturn;\n'
        b'\t\t}\n'
        b'#endif\n\t\t'
        + cap_marker
    )
    convex_cpp = _replace_once(convex_cpp, cap_marker, cap_inject, 'btConvexConvexAlgorithm.cpp')
    gjk_marker = b'gjkPairDetector.getClosestPoints(input,*resultOut,dispatchInfo.m_debugDraw);'
    gjk_inject = (
        b'#ifdef MMD_BULLET_RIGID_PARALLEL\n'
        b'\tauto* ready = dynamic_cast<mmd_bullet_parallel::PreparedResult*>(resultOut);\n'
        b'\tif (!ready || m_numPerturbationIterations || !ready->replayGjk(*m_simplexSolver))\n'
        b'\t\t' + gjk_marker + b'\n'
        b'#else\n'
        b'\t' + gjk_marker + b'\n'
        b'#endif'
    )
    convex_cpp = _replace_once(convex_cpp, gjk_marker, gjk_inject, 'btConvexConvexAlgorithm.cpp')
    (disp_dst_dir / 'btConvexConvexAlgorithm.cpp').write_bytes(
        b'// ALTERED VERSION: rigid-only parallel narrowphase precomputed contact replay.\n' + convex_cpp)

    # BulletDynamics/Dynamics/btDiscreteDynamicsWorld.cpp: binary search island constraints & TLS counter
    world_src = vendor_src / 'BulletDynamics/Dynamics/btDiscreteDynamicsWorld.cpp'
    world_dst = compat_upstream / 'BulletDynamics/Dynamics/btDiscreteDynamicsWorld.cpp'
    _copy_headers(world_src.parent, world_dst.parent)
    world_data = world_src.read_bytes()
    world_data = _replace_once(world_data, b'int gNumClampedCcdMotions=0;', b'thread_local int gNumClampedCcdMotions=0;', 'btDiscreteDynamicsWorld.cpp')
    island_pred = b'class btSortConstraintOnIslandPredicate'
    island_helper = (
        b'#include <algorithm>\n\n'
        b'static inline int mmdFindIslandConstraints(btTypedConstraint** sorted, int count, int island,\n'
        b'                                         btTypedConstraint**& first) {\n'
        b'    first = nullptr;\n'
        b'    if (count == 0) return 0;\n'
        b'    const auto end = sorted + count;\n'
        b'    const auto begin = std::lower_bound(sorted, end, island,\n'
        b'        [](const btTypedConstraint* joint, int key) { return btGetConstraintIslandId(joint) < key; });\n'
        b'    if (begin == end || btGetConstraintIslandId(*begin) != island) return 0;\n'
        b'    first = begin;\n'
        b'    return static_cast<int>(std::upper_bound(begin, end, island,\n'
        b'        [](int key, const btTypedConstraint* joint) { return key < btGetConstraintIslandId(joint); }) - begin);\n'
        b'}\n\n'
        + island_pred
    )
    world_data = _replace_once(world_data, island_pred, island_helper, 'btDiscreteDynamicsWorld.cpp')
    loop_start_marker = b'int numCurConstraints = 0;'
    loop_end_marker = b'///only call solveGroup'
    assert world_data.count(loop_start_marker) == 1 and world_data.count(loop_end_marker) == 1
    loop_start = world_data.index(loop_start_marker)
    loop_end = world_data.index(loop_end_marker, loop_start)
    world_data = (
        world_data[:loop_start]
        + b'int numCurConstraints = mmdFindIslandConstraints(m_sortedConstraints, m_numConstraints, islandId, startConstraint);\n\t\t\t\t'
        + world_data[loop_end:]
    )
    world_dst.write_bytes(
        b'// ALTERED VERSION: binary-search island constraints and thread_local counters.\n'
        b'#include "../../BulletCollision/BroadphaseCollision/btAxisSweep3.h"\n'
        + world_data)

    # BulletCollision/NarrowPhaseCollision/btGjkPairDetector.cpp: thread_local counters
    gjk_src = vendor_src / 'BulletCollision/NarrowPhaseCollision/btGjkPairDetector.cpp'
    gjk_dst = compat_upstream / 'BulletCollision/NarrowPhaseCollision/btGjkPairDetector.cpp'
    _copy_headers(gjk_src.parent, gjk_dst.parent)
    gjk_data = gjk_src.read_bytes()
    gjk_data = _replace_once(gjk_data, b'int gNumDeepPenetrationChecks = 0;', b'thread_local int gNumDeepPenetrationChecks = 0;', 'btGjkPairDetector.cpp')
    gjk_data = _replace_once(gjk_data, b'int gNumGjkChecks = 0;', b'thread_local int gNumGjkChecks = 0;', 'btGjkPairDetector.cpp')
    gjk_dst.write_bytes(b'// ALTERED VERSION: thread_local GJK diagnostic counters.\n' + gjk_data)

    # Original MMD 9.32 RVA 1140E5/114103/11410E evaluates reciprocal,
    # penetration*ERP, then their product. The upstream division-last tree
    # differs by one binary32 RHS ULP in the capsule restitution fixture.
    solver=n.parents[1]/'third_party/bullet-2.75/src/BulletDynamics/ConstraintSolver/btSequentialImpulseConstraintSolver.cpp'
    solver_original=solver.read_bytes()
    solver_before=b'positionalError = -penetration * infoGlobal.m_erp/infoGlobal.m_timeStep;'
    solver_after=b'positionalError = (btScalar(-1.) / infoGlobal.m_timeStep) * (penetration * infoGlobal.m_erp);'
    assert solver_original.count(solver_before)==1
    solver_compiled_data = solver_original.replace(solver_before, solver_after)
    solver_compiled_data = _replace_once(solver_compiled_data, b'int\t\tgNumSplitImpulseRecoveries = 0;', b'thread_local int\t\tgNumSplitImpulseRecoveries = 0;', 'btSequentialImpulseConstraintSolver.cpp')
    compiled_solver=compat_upstream/'BulletDynamics/ConstraintSolver'/solver.name
    compiled_solver.parent.mkdir(parents=True,exist_ok=True)
    # Preserve local quoted-include lookup when compiling outside the vendor tree.
    for header_file in solver.parent.glob('*.h'):
        (compiled_solver.parent/header_file.name).write_bytes(header_file.read_bytes())
    compiled_solver.write_bytes(b'// ALTERED VERSION: MMD native binary32 contact positional-error evaluation order and thread_local counter.\n'+solver_compiled_data)
    patches = dict(buildCompatibilityPatch=dict(file='btSoftBodyInternals.h',before=before.decode(),after=after.decode(),originalSha256=hashlib.sha256(original).hexdigest(),compiledSha256=hashlib.sha256((compat_soft/header.name).read_bytes()).hexdigest()),planeSignedDistanceFix=dict(file='btSparseSDF.h',before=sdf_before.decode(),after=sdf_after.decode(),originalSha256=hashlib.sha256(sdf_original).hexdigest(),compiledSha256=hashlib.sha256((compat_soft/sdf.name).read_bytes()).hexdigest()))
    patches['contactPositionEvaluationOrderFix']=dict(file='BulletDynamics/ConstraintSolver/'+solver.name,before=solver_before.decode(),after=solver_after.decode(),originalSha256=hashlib.sha256(solver_original).hexdigest(),compiledSha256=hashlib.sha256(compiled_solver.read_bytes()).hexdigest(),compiledPath=str(compiled_solver.resolve()),evidence='Original 9.32 RVA 1140E5/114103/11410E; retained bullet_source_review/overlay_audit.json and physics-original/arithmetic-regression.json')
    patches['clusterSeedIndexOverflowFix']=dict(file='BulletSoftBody/'+soft_body.name,before=seed_before.decode(),after=seed_after.decode(),originalSha256=hashlib.sha256(soft_body_original).hexdigest(),compiledSha256=hashlib.sha256(compiled_soft_body.read_bytes()).hexdigest(),compiledPath=str(compiled_soft_body.resolve()),evidence='P1 input-boundary review: 71888*29873 exceeds signed int32 before modulo; positive cluster count alone does not bound node count.')
    # The retained rotating box/capsule fixture first diverges in the EPA GJK
    # triangle projection (original RVA 100670): shared reciprocal then multiply.
    # The same original expression tree is present for tetrahedron weights
    # (100B90), EPA face distance (1024A0), and final EPA weights (103550).
    # Keep vendor source intact and mark the generated translation unit altered.
    epa=n.parents[1]/'third_party/bullet-2.75/src/BulletCollision/NarrowPhaseCollision/btGjkEpa2.cpp'
    epa_original=epa.read_bytes();epa_compiled=epa_original;epa_rules=[]
    for before_expression,after_expression in (
        (b'(btCross(dl[1],b-p)).length()/s',b'(btCross(dl[1],b-p)).length()*(btScalar(1.)/s)'),
        (b'(btCross(dl[2],c-p)).length()/s',b'(btCross(dl[2],c-p)).length()*(btScalar(1.)/s)'),
        (b'det(c,b,d)/vl',b'det(c,b,d)*(btScalar(1.)/vl)'),
        (b'det(a,c,d)/vl',b'det(a,c,d)*(btScalar(1.)/vl)'),
        (b'det(b,a,d)/vl',b'det(b,a,d)*(btScalar(1.)/vl)'),
        (b'btDot(a->w,face->n)/l',b'btDot(a->w,face->n)*(btScalar(1.)/l)'),
        (b'm_result.p[0]\t/=\tsum;',b'm_result.p[0]\t*=\t(btScalar(1.)/sum);'),
        (b'm_result.p[1]\t/=\tsum;',b'm_result.p[1]\t*=\t(btScalar(1.)/sum);'),
        (b'm_result.p[2]\t/=\tsum;',b'm_result.p[2]\t*=\t(btScalar(1.)/sum);'),
    ):
        assert epa_compiled.count(before_expression)==1, before_expression
        epa_compiled=epa_compiled.replace(before_expression,after_expression)
        epa_rules.append(dict(before=before_expression.decode(),after=after_expression.decode()))
    compiled_epa=compat_upstream/'BulletCollision/NarrowPhaseCollision'/epa.name
    compiled_epa.parent.mkdir(parents=True,exist_ok=True)
    for header_file in epa.parent.glob('*.h'):
        (compiled_epa.parent/header_file.name).write_bytes(header_file.read_bytes())
    compiled_epa.write_bytes(b'// ALTERED VERSION: MMD binary32 GJK/EPA shared-reciprocal evaluation order.\n'+epa_compiled)
    patches['gjkEpaEvaluationOrderFix']=dict(file='BulletCollision/NarrowPhaseCollision/'+epa.name,expressions=epa_rules,originalSha256=hashlib.sha256(epa_original).hexdigest(),compiledSha256=hashlib.sha256(compiled_epa.read_bytes()).hexdigest(),compiledPath=str(compiled_epa.resolve()),evidence='artifacts/stage-20261021-contact-precision/triangle-projection.json; original RVA 100670/100B90/1024A0/103550')
    return compat_upstream, compat_soft, patches

def prepared_compile_files(files, native, compat_upstream, compat_soft):
    """Resolve the exact generated units used by both native builds."""
    vendor_src = native.parents[1] / 'third_party/bullet-2.75/src'
    mapped = []
    for p in files:
        if p.parent == native / 'upstream/BulletSoftBody':
            mapped.append(compat_soft / p.name)
        elif p == native / 'mmd_bullet_soft.cpp' and (compat_upstream / p.name).is_file():
            mapped.append(compat_upstream / p.name)
        else:
            try:
                rel = p.relative_to(vendor_src)
                candidate = compat_upstream / rel
                mapped.append(candidate if candidate.is_file() else p)
            except ValueError:
                mapped.append(p)
    return mapped

def source_identity(root, compat_soft):
    """Hash every source/header used by either bridge, plus build preparation."""
    paths = set()
    for directory in (root/'third_party/bullet-2.75/src', root/'native/Mmd.Modern.Bullet', root/'native/Mmd.Modern.Bullet.SoftBody', compat_soft.parent):
        paths.update(p for p in directory.rglob('*') if p.suffix in ('.cpp', '.h'))
    paths.update(root/'scripts'/name for name in ('build-bullet.py', 'build-bullet-soft.py', 'bullet_soft_sources.py'))
    def label(p):
        try: return p.relative_to(root).as_posix()
        except ValueError: return str(p.resolve())
    return [dict(path=label(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in sorted(paths)]

if __name__ == '__main__':
    import argparse
    p=argparse.ArgumentParser();p.add_argument('--native',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    a.output.mkdir(parents=True,exist_ok=True);prepare_soft_sources(a.native,a.output)
