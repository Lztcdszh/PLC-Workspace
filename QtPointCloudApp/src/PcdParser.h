#pragma once

#include <QString>
#include <vtkSmartPointer.h>
#include <vtkPolyData.h>

// Minimal ASCII PCD parser (the binary/compressed variants are not handled).
// Supports FIELDS containing x y z and either rgb (packed float) or r g b.
class PcdParser
{
public:
    static vtkSmartPointer<vtkPolyData> parse(const QString& path);
};
