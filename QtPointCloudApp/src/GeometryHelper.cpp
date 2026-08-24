#include "GeometryHelper.h"

#include <vector>
#include <cmath>
#include <algorithm>

#include <vtkPoints.h>
#include <vtkHull.h>
#include <vtkMassProperties.h>
#include <vtkClipPolyData.h>
#include <vtkBox.h>
#include <vtkPlane.h>

double GeometryHelper::computeVoxelVolume(vtkPolyData* poly, double spacing)
{
    auto pts = poly->GetPoints();
    if (!pts || pts->GetNumberOfPoints() == 0) return 0.0;

    double b[6];
    poly->GetBounds(b);
    int nx = std::max(1, (int)std::ceil((b[1] - b[0]) / spacing));
    int ny = std::max(1, (int)std::ceil((b[3] - b[2]) / spacing));
    int nz = std::max(1, (int)std::ceil((b[5] - b[4]) / spacing));
    double sx = (b[1] - b[0]) / nx;
    double sy = (b[3] - b[2]) / ny;
    double sz = (b[5] - b[4]) / nz;
    double voxVol = sx * sy * sz;

    // Occupancy grid: mark which voxels contain at least one point.
    std::vector<std::vector<std::vector<char>>> grid(
        nx, std::vector<std::vector<char>>(ny, std::vector<char>(nz, 0)));
    for (vtkIdType i = 0; i < pts->GetNumberOfPoints(); ++i) {
        double p[3];
        pts->GetPoint(i, p);
        int ix = std::min(nx - 1, (int)((p[0] - b[0]) / sx));
        int iy = std::min(ny - 1, (int)((p[1] - b[2]) / sy));
        int iz = std::min(nz - 1, (int)((p[2] - b[4]) / sz));
        if (ix >= 0 && iy >= 0 && iz >= 0) grid[ix][iy][iz] = 1;
    }

    // For each (x,y) column, fill everything between the lowest and highest
    // occupied voxel (column-fill approximation of a solid volume).
    long long occupied = 0;
    for (int ix = 0; ix < nx; ++ix) {
        for (int iy = 0; iy < ny; ++iy) {
            int zmin = -1, zmax = -1;
            for (int iz = 0; iz < nz; ++iz) {
                if (grid[ix][iy][iz]) {
                    if (zmin < 0) zmin = iz;
                    zmax = iz;
                }
            }
            if (zmin >= 0) occupied += (zmax - zmin + 1);
        }
    }
    return occupied * voxVol;
}

double GeometryHelper::computeConvexHullVolume(vtkPolyData* poly)
{
    auto hull = vtkSmartPointer<vtkHull>::New();
    hull->SetInputData(poly);
    hull->AddRecursiveSpherePlanes(5);
    hull->Update();

    auto mp = vtkSmartPointer<vtkMassProperties>::New();
    mp->SetInputConnection(hull->GetOutputPort());
    mp->Update();
    return mp->GetVolume();
}

double GeometryHelper::computeBoundingBoxVolume(vtkPolyData* poly)
{
    double b[6];
    poly->GetBounds(b);
    return (b[1] - b[0]) * (b[3] - b[2]) * (b[5] - b[4]);
}

vtkSmartPointer<vtkPolyData> GeometryHelper::clipBox(vtkPolyData* poly,
    double min[3], double max[3], bool invert)
{
    auto box = vtkSmartPointer<vtkBox>::New();
    box->SetBounds(min[0], max[0], min[1], max[1], min[2], max[2]);

    auto clip = vtkSmartPointer<vtkClipPolyData>::New();
    clip->SetInputData(poly);
    clip->SetClipFunction(box);
    clip->SetInsideOut(invert ? 1 : 0);
    clip->Update();

    auto out = vtkSmartPointer<vtkPolyData>::New();
    out->ShallowCopy(clip->GetOutput());
    return out;
}

vtkSmartPointer<vtkPolyData> GeometryHelper::clipPlane(vtkPolyData* poly,
    double normal[3], double origin[3], bool invert)
{
    auto plane = vtkSmartPointer<vtkPlane>::New();
    plane->SetNormal(normal[0], normal[1], normal[2]);
    plane->SetOrigin(origin[0], origin[1], origin[2]);

    auto clip = vtkSmartPointer<vtkClipPolyData>::New();
    clip->SetInputData(poly);
    clip->SetClipFunction(plane);
    clip->SetInsideOut(invert ? 1 : 0);
    clip->Update();

    auto out = vtkSmartPointer<vtkPolyData>::New();
    out->ShallowCopy(clip->GetOutput());
    return out;
}
