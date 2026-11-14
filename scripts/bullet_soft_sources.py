"""Cache-only compatibility preparation shared by standalone and coupled builds."""
from pathlib import Path
import hashlib

def prepare_soft_sources(n, output_directory):
    out = Path(output_directory) / 'unused'
    # Modern Clang rejects the old uninitialized const aggregate template. Preserve
    # vendored bytes and compile a documented cache-only value-initialized copy.
    compat=out.parent/'compat';compat.mkdir(exist_ok=True)
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
    # Original MMD 9.32 RVA 1140E5/114103/11410E evaluates reciprocal,
    # penetration*ERP, then their product. The upstream division-last tree
    # differs by one binary32 RHS ULP in the capsule restitution fixture.
    solver=n.parents[1]/'third_party/bullet-2.75/src/BulletDynamics/ConstraintSolver/btSequentialImpulseConstraintSolver.cpp'
    solver_original=solver.read_bytes()
    solver_before=b'positionalError = -penetration * infoGlobal.m_erp/infoGlobal.m_timeStep;'
    solver_after=b'positionalError = (btScalar(-1.) / infoGlobal.m_timeStep) * (penetration * infoGlobal.m_erp);'
    assert solver_original.count(solver_before)==1
    compiled_solver=compat_upstream/'BulletDynamics/ConstraintSolver'/solver.name
    compiled_solver.parent.mkdir(parents=True,exist_ok=True)
    # Preserve local quoted-include lookup when compiling outside the vendor tree.
    for header_file in solver.parent.glob('*.h'):
        (compiled_solver.parent/header_file.name).write_bytes(header_file.read_bytes())
    compiled_solver.write_bytes(b'// ALTERED VERSION: MMD native binary32 contact positional-error evaluation order.\n'+solver_original.replace(solver_before,solver_after))
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
    solver=native.parents[1]/'third_party/bullet-2.75/src/BulletDynamics/ConstraintSolver/btSequentialImpulseConstraintSolver.cpp'
    epa=native.parents[1]/'third_party/bullet-2.75/src/BulletCollision/NarrowPhaseCollision/btGjkEpa2.cpp'
    return [compat_upstream/'BulletDynamics/ConstraintSolver'/p.name if p==solver else compat_upstream/'BulletCollision/NarrowPhaseCollision'/p.name if p==epa else compat_soft/p.name if p.parent==native/'upstream/BulletSoftBody' else p for p in files]

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
