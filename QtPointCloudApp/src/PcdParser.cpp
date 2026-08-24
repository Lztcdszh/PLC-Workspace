#include "PcdParser.h"

#include <QFile>
#include <QTextStream>
#include <QStringList>
#include <QRegularExpression>
#include <vtkPoints.h>
#include <vtkCellArray.h>
#include <vtkUnsignedCharArray.h>
#include <vtkPointData.h>
#include <vtkPolyData.h>

vtkSmartPointer<vtkPolyData> PcdParser::parse(const QString& path)
{
    QFile f(path);
    if (!f.open(QIODevice::ReadOnly | QIODevice::Text))
        return nullptr;

    QTextStream in(&f);
    in.setEncoding(QStringConverter::Utf8);

    QStringList fields;
    int width = 0, height = 0, points = 0;
    QString dataMode;
    bool headerDone = false;

    // Parse header (lines until "DATA ...")
    while (!in.atEnd()) {
        QString line = in.readLine().trimmed();
        if (line.isEmpty() || line.startsWith("#")) continue;
        QStringList toks = line.split(QRegularExpression("\\s+"), Qt::SkipEmptyParts);
        if (toks.isEmpty()) continue;
        QString key = toks[0];
        if (key == "FIELDS") {
            for (int i = 1; i < toks.size(); ++i) fields << toks[i];
        } else if (key == "WIDTH") {
            width = toks[1].toInt();
        } else if (key == "HEIGHT") {
            height = toks[1].toInt();
        } else if (key == "POINTS") {
            points = toks[1].toInt();
        } else if (key == "DATA") {
            dataMode = toks[1];
            headerDone = true;
            break;
        }
    }

    if (!headerDone || dataMode != "ascii")
        return nullptr;

    int n = (width > 0 && height > 0) ? width * height : points;
    if (n <= 0) return nullptr;

    int xi = fields.indexOf("x");
    int yi = fields.indexOf("y");
    int zi = fields.indexOf("z");
    int rgbi = fields.indexOf("rgb");
    int ri = fields.indexOf("r");
    int gi = fields.indexOf("g");
    int bi = fields.indexOf("b");
    if (xi < 0 || yi < 0 || zi < 0)
        return nullptr;

    auto pts = vtkSmartPointer<vtkPoints>::New();
    auto colors = vtkSmartPointer<vtkUnsignedCharArray>::New();
    colors->SetName("Colors");
    colors->SetNumberOfComponents(3);

    bool hasColor = (rgbi >= 0) || (ri >= 0 && gi >= 0 && bi >= 0);

    for (int i = 0; i < n && !in.atEnd(); ++i) {
        QString line = in.readLine().trimmed();
        if (line.isEmpty()) { --i; continue; }
        QStringList vals = line.split(QRegularExpression("\\s+"), Qt::SkipEmptyParts);
        if (vals.size() < fields.size()) { --i; continue; }

        double x = vals[xi].toDouble();
        double y = vals[yi].toDouble();
        double z = vals[zi].toDouble();
        pts->InsertNextPoint(x, y, z);

        if (hasColor) {
            unsigned char cr = 255, cg = 255, cb = 255;
            if (rgbi >= 0) {
                // rgb stored as a packed float (PCL convention)
                union { float f; uint32_t u; } conv;
                conv.f = vals[rgbi].toFloat();
                uint32_t packed = conv.u;
                cr = (packed >> 16) & 0xFF;
                cg = (packed >> 8) & 0xFF;
                cb = packed & 0xFF;
            } else {
                cr = (unsigned char)std::min(255.0, vals[ri].toDouble());
                cg = (unsigned char)std::min(255.0, vals[gi].toDouble());
                cb = (unsigned char)std::min(255.0, vals[bi].toDouble());
            }
            colors->InsertNextTuple3(cr, cg, cb);
        }
    }

    auto poly = vtkSmartPointer<vtkPolyData>::New();
    poly->SetPoints(pts);
    if (hasColor) poly->GetPointData()->SetScalars(colors);
    return poly;
}
