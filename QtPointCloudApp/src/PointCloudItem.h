#pragma once

#include <QString>
#include <QColor>
#include <vtkSmartPointer.h>
#include <vtkPolyData.h>
#include <vtkActor.h>

// Color modes mirror the WPF version: use original RGB, color by height, or a solid color.
enum class ColorMode { Original, Height, Solid };

// A single point cloud loaded into the scene.
struct PointCloudItem
{
    QString name;
    QColor displayColor = Qt::cyan;
    ColorMode colorMode = ColorMode::Original;
    bool visible = true;

    // Original (untransformed) point data; kept so we can re-color or re-clip.
    vtkSmartPointer<vtkPolyData> data;
    // Actor currently shown in the scene.
    vtkSmartPointer<vtkActor> actor;
    // Optional white bounding-box wireframe.
    vtkSmartPointer<vtkActor> boxActor;

    double bounds[6] = {0.0, 0.0, 0.0, 0.0, 0.0, 0.0};
};
