#pragma once

#include <vtkSmartPointer.h>
#include <vtkPolyData.h>

// Geometric utilities: volume estimation and clipping. Mirrors the WPF
// GeometryHelper / VolumeCalculator behavior using VTK C++ classes.
class GeometryHelper
{
public:
    // Occupied-voxel volume using the column-fill approximation (matches WPF).
    static double computeVoxelVolume(vtkPolyData* poly, double spacing = 0.02);

    // Convex-hull volume via supporting planes + vtkMassProperties.
    static double computeConvexHullVolume(vtkPolyData* poly);

    // Axis-aligned bounding box volume.
    static double computeBoundingBoxVolume(vtkPolyData* poly);

    // Keep points inside [min,max] (or outside when invert=true).
    static vtkSmartPointer<vtkPolyData> clipBox(vtkPolyData* poly,
        double min[3], double max[3], bool invert = false);

    // Keep points on the side where (x-origin)·normal <= 0 (or opposite if invert).
    static vtkSmartPointer<vtkPolyData> clipPlane(vtkPolyData* poly,
        double normal[3], double origin[3], bool invert = false);
};
