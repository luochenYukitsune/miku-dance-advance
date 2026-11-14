/* Independently authored MDA bridge. The unmodified MikkTSpace implementation
 * in upstream/ is by Morten S. Mikkelsen; see LICENSE.MikkTSpace.txt. */
#include <math.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "upstream/mikktspace.h"

#if defined(_WIN32)
#define MMD_EXPORT __declspec(dllexport)
#else
#define MMD_EXPORT __attribute__((visibility("default")))
#endif

/* ABI 1: tightly packed float3 position/normal, float2 UV, uint32 index,
 * float4 output per index/corner. No index welding or rewriting at this boundary.
 * Status: 0 success, 1 bad argument/budget, 2 invalid numerical input,
 * 3 generator failure, 4 invalid generator output. */
#define MMD_MAX_CORNERS 1000000
#define MMD_MAX_VERTICES 1000000
typedef struct {
    const float *positions;
    const float *normals;
    const float *uvs;
    const uint32_t *indices;
    float *tangents;
    int index_count;
} MmdMikkData;

static const MmdMikkData *data(const SMikkTSpaceContext *context)
{ return (const MmdMikkData *)context->m_pUserData; }
static int faces(const SMikkTSpaceContext *context) { return data(context)->index_count / 3; }
static int vertices(const SMikkTSpaceContext *context, int face) { (void)context; (void)face; return 3; }
static size_t vertex(const SMikkTSpaceContext *context, int face, int corner)
{ return data(context)->indices[(size_t)face * 3 + (size_t)corner]; }
static void position(const SMikkTSpaceContext *context, float output[], int face, int corner)
{ memcpy(output, data(context)->positions + vertex(context, face, corner) * 3, 3 * sizeof(float)); }
static void normal(const SMikkTSpaceContext *context, float output[], int face, int corner)
{ memcpy(output, data(context)->normals + vertex(context, face, corner) * 3, 3 * sizeof(float)); }
static void uv(const SMikkTSpaceContext *context, float output[], int face, int corner)
{ memcpy(output, data(context)->uvs + vertex(context, face, corner) * 2, 2 * sizeof(float)); }
static void tangent(const SMikkTSpaceContext *context, const float value[], float sign, int face, int corner)
{
    float *target = data(context)->tangents + ((size_t)face * 3 + (size_t)corner) * 4;
    memcpy(target, value, 3 * sizeof(float)); target[3] = sign;
}

MMD_EXPORT int mmd_mikktspace_abi_version(void) { return 1; }

MMD_EXPORT int mmd_mikktspace_generate(const float *positions, const float *normals,
    const float *uvs, int vertex_count, const uint32_t *indices, int index_count,
    float *corner_tangents, int output_corner_count)
{
    SMikkTSpaceInterface api;
    SMikkTSpaceContext context;
    MmdMikkData mesh;
    int i, j;
    if (!positions || !normals || !uvs || !indices || !corner_tangents ||
        vertex_count <= 0 || vertex_count > MMD_MAX_VERTICES ||
        index_count <= 0 || index_count > MMD_MAX_CORNERS || index_count % 3 != 0 ||
        output_corner_count != index_count) return 1;
    for (i = 0; i < index_count; ++i) {
        size_t index = indices[i];
        double length;
        if (index >= (size_t)vertex_count) return 1;
        for (j = 0; j < 3; ++j)
            if (!isfinite(positions[index * 3 + (size_t)j]) || !isfinite(normals[index * 3 + (size_t)j])) return 2;
        if (!isfinite(uvs[index * 2]) || !isfinite(uvs[index * 2 + 1])) return 2;
        length = (double)normals[index * 3] * normals[index * 3] +
            (double)normals[index * 3 + 1] * normals[index * 3 + 1] +
            (double)normals[index * 3 + 2] * normals[index * 3 + 2];
        if (length < 0.999 || length > 1.001) return 2;
    }
    mesh.positions = positions; mesh.normals = normals; mesh.uvs = uvs;
    mesh.indices = indices; mesh.tangents = corner_tangents; mesh.index_count = index_count;
    /* NaN initialization also detects any output corner missed by the generator. */
    for (i = 0; i < index_count * 4; ++i) corner_tangents[i] = NAN;
    memset(&api, 0, sizeof(api));
    api.m_getNumFaces = faces; api.m_getNumVerticesOfFace = vertices;
    api.m_getPosition = position; api.m_getNormal = normal; api.m_getTexCoord = uv;
    api.m_setTSpaceBasic = tangent;
    context.m_pInterface = &api; context.m_pUserData = &mesh;
    if (!genTangSpaceDefault(&context)) return 3;
    for (i = 0; i < index_count; ++i) {
        const float *value = corner_tangents + (size_t)i * 4;
        double length;
        for (j = 0; j < 4; ++j) if (!isfinite(value[j])) return 4;
        length = (double)value[0] * value[0] + (double)value[1] * value[1] + (double)value[2] * value[2];
        if (length < 0.999 || length > 1.001 || (value[3] != 1.0f && value[3] != -1.0f)) return 4;
    }
    return 0;
}
