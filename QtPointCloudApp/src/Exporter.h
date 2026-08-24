#pragma once

#include <QString>
#include <vtkRenderWindow.h>

// Capture the render window to an image file. PNG / JPG / BMP is chosen by
// the file extension.
class Exporter
{
public:
    static bool exportImage(vtkRenderWindow* win, const QString& filePath);
};
