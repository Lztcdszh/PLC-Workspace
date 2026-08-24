#pragma once

#include <QObject>
#include <QColor>
#include <vector>
#include <vtkSmartPointer.h>
#include <vtkRenderer.h>
#include <vtkRenderWindow.h>
#include <vtkActor.h>
#include <vtkAxesActor.h>
#include "PointCloudItem.h"

// Owns the VTK renderer and the list of point clouds. Central hub for all
// scene operations: add/remove, visibility, coloring, bounding boxes, axes.
class CloudManager : public QObject
{
    Q_OBJECT
public:
    explicit CloudManager(QObject* parent = nullptr);

    void setRenderWindow(vtkRenderWindow* win);
    vtkRenderer* renderer() const { return m_renderer; }

    int addCloud(vtkPolyData* poly, const QString& name, const QColor& color = Qt::cyan);
    void removeCloud(int index);
    void clear();
    int count() const { return static_cast<int>(m_items.size()); }

    void setVisible(int index, bool visible);
    void setColor(int index, const QColor& color);
    void setColorMode(int index, ColorMode mode);
    void setOpacity(int index, double opacity);

    void setBackground(const QColor& color);
    void setAxesVisible(bool visible);
    void setBoundingBoxVisible(int index, bool visible);

    // Re-frame the camera to fit all clouds.
    void frameScene();

    const std::vector<PointCloudItem>& items() const { return m_items; }

    // Build an actor for the given poly data using the item's color mode.
    vtkSmartPointer<vtkActor> buildActor(vtkPolyData* poly, const PointCloudItem& item);

signals:
    void changed();   // scene changed; UI should refresh the cloud list

private:
    std::vector<PointCloudItem> m_items;
    vtkSmartPointer<vtkRenderer> m_renderer;
    vtkSmartPointer<vtkAxesActor> m_axes;
    bool m_axesVisible = true;
};
