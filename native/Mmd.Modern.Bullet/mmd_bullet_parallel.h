#pragma once
#include <btBulletDynamicsCommon.h>
#include <BulletCollision/BroadphaseCollision/btOverlappingPairCache.h>
#include <BulletCollision/NarrowPhaseCollision/btGjkEpaPenetrationDepthSolver.h>
#include <mmd_bullet_ground_bounds.h>
#include <mmd_bullet_pair_query.h>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <xmmintrin.h>

#include <algorithm>
#include <atomic>
#include <cfenv>
#include <cmath>
#include <exception>
#include <memory>
#include <numeric>
#include <typeinfo>
#include <vector>

extern ContactProcessedCallback gContactProcessedCallback;

namespace mmd_bullet_parallel {

inline size_t availableProcessors() noexcept {
    DWORD_PTR processMask = 0;
    DWORD_PTR systemMask = 0;
    if (GetProcessAffinityMask(GetCurrentProcess(), &processMask, &systemMask) && processMask) {
        size_t count = 0;
        for (DWORD_PTR mask = processMask; mask != 0; mask &= (mask - 1)) {
            ++count;
        }
        return count > 0 ? count : 1;
    }
    const DWORD active = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
    return active > 0 ? static_cast<size_t>(active) : 1;
}

class ParallelIslandSolver final : public btSequentialImpulseConstraintSolver {
    using Solver = btSequentialImpulseConstraintSolver;

    struct Job {
        size_t bodyOffset;
        int bodyCount;
        btPersistentManifold** manifolds;
        int manifoldCount;
        btTypedConstraint** joints;
        int jointCount;
        size_t weight;
    };

    struct Lane {
        ParallelIslandSolver& owner;
        Solver scratch;
        PTP_WORK work{};
        std::vector<size_t> jobs;
        size_t weight{};
        std::exception_ptr error;
        explicit Lane(ParallelIslandSolver& owner) : owner(owner) {}
        ~Lane() {
            if (work) CloseThreadpoolWork(work);
        }
    };

    std::vector<Job> jobs;
    std::vector<btCollisionObject*> bodies;
    std::vector<size_t> order;
    std::vector<std::unique_ptr<Lane>> lanes;
    const btContactSolverInfo* settings{};
    btDispatcher* dispatcher{};
    btStackAlloc* stack{};
    bool independent{};
    std::fenv_t fpEnvironment{};
    unsigned mxcsr{};
    const size_t cpuLimit;

    static bool compatibleBody(const btCollisionObject* object, int island) noexcept {
        const auto* body = btRigidBody::upcast(object);
        // Zero-inverse-mass endpoints map to each solver's private fixed body.
        return body && (!body->getInvMass() || (island >= 0 && body->getIslandTag() == island));
    }

    void solve(const Job& job, Solver& solver) {
        solver.Solver::solveGroup(bodies.data() + job.bodyOffset, job.bodyCount, job.manifolds,
            job.manifoldCount, job.joints, job.jointCount, *settings, nullptr, stack, dispatcher);
    }

    void run(Lane& lane) noexcept {
        try {
            for (size_t index : lane.jobs) solve(jobs[index], lane.scratch);
        } catch (...) {
            lane.error = std::current_exception();
        }
    }

    static void CALLBACK worker(PTP_CALLBACK_INSTANCE, void* context, PTP_WORK) noexcept {
        auto& lane = *static_cast<Lane*>(context);
        std::fenv_t saved;
        std::fegetenv(&saved);
        const unsigned savedMxcsr = _mm_getcsr();
        std::fesetenv(&lane.owner.fpEnvironment);
        _mm_setcsr(lane.owner.mxcsr);
        lane.owner.run(lane);
        std::fesetenv(&saved);
        _mm_setcsr(savedMxcsr);
    }

public:
    ParallelIslandSolver() : cpuLimit(availableProcessors()) {}

    void prepareSolve(int, int) override {
        jobs.clear();
        bodies.clear();
        independent = true;
    }

    btScalar solveGroup(btCollisionObject** group, int count, btPersistentManifold** manifolds, int manifoldCount,
                        btTypedConstraint** joints, int jointCount, const btContactSolverInfo& info,
                        btIDebugDraw* debug, btStackAlloc* allocator, btDispatcher* dispatch) override {
        if (cpuLimit == 1 || count <= 0 || debug || info.m_splitImpulse || (info.m_solverMode & SOLVER_RANDMIZE_ORDER))
            return Solver::solveGroup(group, count, manifolds, manifoldCount, joints, jointCount, info, debug, allocator, dispatch);
        settings = &info;
        stack = allocator;
        dispatcher = dispatch;
        const int island = group[0]->getIslandTag();
        size_t weight = static_cast<size_t>(jointCount) + 1;
        for (int i = 0; i < manifoldCount; ++i) {
            const auto& m = *manifolds[i];
            weight += static_cast<size_t>(m.getNumContacts());
            independent = independent && compatibleBody(static_cast<const btCollisionObject*>(m.getBody0()), island)
                                      && compatibleBody(static_cast<const btCollisionObject*>(m.getBody1()), island);
        }
        for (int i = 0; i < jointCount && independent; ++i) {
            independent = compatibleBody(&joints[i]->getRigidBodyA(), island)
                       && compatibleBody(&joints[i]->getRigidBodyB(), island);
        }
        jobs.push_back({bodies.size(), count, manifolds, manifoldCount, joints, jointCount, weight});
        bodies.insert(bodies.end(), group, group + count);
        return 0;
    }

    void allSolved(const btContactSolverInfo&, btIDebugDraw*, btStackAlloc*) override {
        if (jobs.empty()) return;
        size_t total = 0;
        size_t largest = 0;
        for (const auto& job : jobs) {
            total += job.weight;
            largest = std::max(largest, job.weight);
        }
        const size_t count = independent ? std::min({jobs.size(), cpuLimit, (total + largest - 1) / largest}) : 1;
        if (count <= 1) {
            for (const auto& job : jobs) solve(job, *this);
            return;
        }
        while (lanes.size() < count) {
            auto lane = std::make_unique<Lane>(*this);
            if (!lanes.empty()) {
                lane->work = CreateThreadpoolWork(worker, lane.get(), nullptr);
                if (!lane->work) {
                    for (const auto& job : jobs) solve(job, *this);
                    return;
                }
            }
            lanes.push_back(std::move(lane));
        }
        order.resize(jobs.size());
        std::iota(order.begin(), order.end(), size_t(0));
        std::sort(order.begin(), order.end(), [&](size_t a, size_t b) { return jobs[a].weight > jobs[b].weight; });
        for (size_t i = 0; i < count; ++i) {
            lanes[i]->jobs.clear();
            lanes[i]->weight = 0;
            lanes[i]->error = nullptr;
        }
        for (size_t index : order) {
            auto& lane = **std::min_element(lanes.begin(), lanes.begin() + count,
                [](const auto& a, const auto& b) { return a->weight < b->weight; });
            lane.jobs.push_back(index);
            lane.weight += jobs[index].weight;
        }
        std::fegetenv(&fpEnvironment);
        mxcsr = _mm_getcsr();
        for (size_t i = 1; i < count; ++i) SubmitThreadpoolWork(lanes[i]->work);
        run(*lanes[0]);
        for (size_t i = 1; i < count; ++i) WaitForThreadpoolWorkCallbacks(lanes[i]->work, FALSE);
        for (size_t i = 0; i < count; ++i) {
            if (lanes[i]->error) std::rethrow_exception(lanes[i]->error);
        }
    }
};

class ParallelCollisionDispatcher final : public btCollisionDispatcher {
    std::vector<Query> jobs;
    std::atomic<size_t> next{};
    size_t chunk{};
    const size_t cpuLimit;
    PTP_WORK work;
    std::fenv_t fp{};
    unsigned mxcsr{};
    SRWLOCK errorLock = SRWLOCK_INIT;
    std::exception_ptr error;

    static bool supportedPrimitive(const btCollisionObject& body) noexcept {
        const int shape = body.getCollisionShape()->getShapeType();
        return shape == SPHERE_SHAPE_PROXYTYPE || shape == BOX_SHAPE_PROXYTYPE || shape == CAPSULE_SHAPE_PROXYTYPE;
    }

    void compute() noexcept {
        try {
            btVoronoiSimplexSolver simplex{};
            simplex.m_cachedP1.setZero();
            simplex.m_cachedP2.setZero();
            simplex.m_cachedV.setZero();
            btGjkEpaPenetrationDepthSolver penetration;
            for (;;) {
                const size_t first = next.fetch_add(chunk, std::memory_order_relaxed);
                if (first >= jobs.size()) return;
                const size_t end = std::min(first + chunk, jobs.size());
                for (size_t i = first; i < end; ++i) jobs[i].compute(simplex, penetration);
            }
        } catch (...) {
            AcquireSRWLockExclusive(&errorLock);
            if (!error) error = std::current_exception();
            ReleaseSRWLockExclusive(&errorLock);
        }
    }

    static void CALLBACK worker(PTP_CALLBACK_INSTANCE, void* data, PTP_WORK) noexcept {
        auto& d = *static_cast<ParallelCollisionDispatcher*>(data);
        std::fenv_t saved;
        std::fegetenv(&saved);
        const unsigned old = _mm_getcsr();
        std::fesetenv(&d.fp);
        _mm_setcsr(d.mxcsr);
        d.compute();
        std::fesetenv(&saved);
        _mm_setcsr(old);
    }

public:
    explicit ParallelCollisionDispatcher(btCollisionConfiguration* config)
        : btCollisionDispatcher(config),
          cpuLimit(availableProcessors()),
          work(CreateThreadpoolWork(worker, this, nullptr)) {}

    ~ParallelCollisionDispatcher() override {
        if (work) CloseThreadpoolWork(work);
    }

    void dispatchAllCollisionPairs(btOverlappingPairCache* cache, const btDispatcherInfo& info, btDispatcher* owner) override {
        if (!work || cpuLimit < 2 || owner != this || getNearCallback() != defaultNearCallback ||
            info.m_dispatchFunc != btDispatcherInfo::DISPATCH_DISCRETE || info.m_useConvexConservativeDistanceUtil ||
            info.m_debugDraw || gContactAddedCallback || gContactDestroyedCallback || gContactProcessedCallback ||
            typeid(*cache) != typeid(btHashedOverlappingPairCache)) {
            btCollisionDispatcher::dispatchAllCollisionPairs(cache, info, owner);
            return;
        }
        auto& pairs = cache->getOverlappingPairArray();
        jobs.clear();
        for (int i = 0; i < pairs.size(); ++i) {
            auto* algorithm = pairs[i].m_algorithm;
            if (!algorithm || typeid(*algorithm) != typeid(btConvexConvexAlgorithm)) continue;
            auto* algo = static_cast<btConvexConvexAlgorithm*>(algorithm);
            if (!algo->getManifold()) continue;
            auto* a = static_cast<btCollisionObject*>(pairs[i].m_pProxy0->m_clientObject);
            auto* b = static_cast<btCollisionObject*>(pairs[i].m_pProxy1->m_clientObject);
            if (supportedPrimitive(*a) && supportedPrimitive(*b) && needsCollision(a, b))
                jobs.emplace_back(i, a, b, algo->getManifold()->getContactBreakingThreshold());
        }
        const size_t lanes = std::min(cpuLimit, static_cast<size_t>(std::sqrt(double(jobs.size()))));
        if (lanes < 2) {
            btCollisionDispatcher::dispatchAllCollisionPairs(cache, info, owner);
            return;
        }
        chunk = std::max<size_t>(1, jobs.size() / (lanes * lanes));
        next.store(0, std::memory_order_relaxed);
        error = nullptr;
        std::fegetenv(&fp);
        mxcsr = _mm_getcsr();
        for (size_t i = 1; i < lanes; ++i) SubmitThreadpoolWork(work);
        compute();
        WaitForThreadpoolWorkCallbacks(work, FALSE);
        if (error) std::rethrow_exception(error);

        size_t j = 0;
        for (int i = 0; i < pairs.size(); ++i) {
            if (j < jobs.size() && jobs[j].ordinal == i) {
                const auto& q = jobs[j++];
                PreparedResult result(q);
                pairs[i].m_algorithm->processCollision(q.a, q.b, info, &result);
            } else {
                defaultNearCallback(pairs[i], *this, info);
            }
        }
    }
};

}
