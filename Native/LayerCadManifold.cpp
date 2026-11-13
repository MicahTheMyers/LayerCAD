#include <algorithm>
#include <memory>
#include <manifold/manifold.h>

using manifold::Manifold;
using manifold::MeshGL64;

extern "C" {

__declspec(dllexport) Manifold* layercad_solid(const double* vertices, size_t coordinates,
                                             const uint64_t* triangles, size_t indices) noexcept {
    try {
        MeshGL64 mesh;
        mesh.vertProperties.assign(vertices, vertices + coordinates);
        mesh.triVerts.assign(triangles, triangles + indices);
        auto solid = std::make_unique<Manifold>(mesh);
        return solid->Status() == Manifold::Error::NoError && !solid->IsEmpty() ? solid.release() : nullptr;
    } catch (...) {
        return nullptr;
    }
}

// Return 0 for no contact, 1 for a result, and -1 if the operation failed.
__declspec(dllexport) int layercad_boolean(const Manifold* stock, const Manifold* tool,
                                           int merge, Manifold** result) noexcept {
    *result = nullptr;
    try {
        auto overlap = *stock ^ *tool;
        if (overlap.Status() != Manifold::Error::NoError)
            return -1;
        const bool overlapping = overlap.Volume() > 1e-10;
        if (!merge && !overlapping)
            return 0;
        auto changed = std::make_unique<Manifold>(merge ? *stock + *tool : *stock - *tool);
        if (changed->Status() != Manifold::Error::NoError)
            return -1;
        if (merge && !overlapping &&
            changed->Decompose().size() >= stock->Decompose().size() + tool->Decompose().size())
            return 0;
        *result = changed.release();
        return 1;
    } catch (...) {
        return -1;
    }
}

__declspec(dllexport) size_t layercad_triangle_count(const Manifold* solid) noexcept {
    return solid->NumTri();
}

__declspec(dllexport) int layercad_read(const Manifold* solid, double* output) noexcept {
    try {
        auto mesh = solid->GetMeshGL64();
        for (auto vertex : mesh.triVerts) {
            auto coordinates = mesh.vertProperties.data() + vertex * mesh.numProp;
            output = std::copy_n(coordinates, 3, output);
        }
        return 1;
    } catch (...) {
        return 0;
    }
}

__declspec(dllexport) void layercad_free(Manifold* solid) noexcept {
    delete solid;
}
}
