#include "CloudManager.h"

#include <vtkPolyData.h>
#include <vtkPointData.h>
#include <vtkVertexGlyphFilter.h>
#include <vtkPolyDataMapper.h>
#include <vtkProperty.h>
#include <vtkElevationFilter.h>
#include <vtkAxesActor.h>
#include <vtkRenderWindow.h>
#include <vtkOutlineFilter.h>

CloudManager::CloudManager(QObject* parent)
    : QObject(parent)
{
    m_renderer = vtkSmartPointer<vtkRenderer>::New();
    m_renderer->SetBackground(0.1, 0.1, 0.1);

    m_axes = vtkSmartPointer<vtkAxesActor>::New();
    m_axes->SetAxisLabels(false);
    m_axes->SetTotalLength(1.0, 1.0, 1.0);
    m_renderer->AddActor(m_axes);
}

void CloudManager::setRenderWindow(vtkRenderWindow* win)
{
    win->AddRenderer(m_renderer);
}

vtkSmartPointer<vtkActor> CloudManager::buildActor(vtkPolyData* poly, const PointCloudItem& item)
{
    auto glyph = vtkSmartPointer<vtkVertexGlyphFilter>::New();
    glyph->SetInputData(poly);
    glyph->Update();

    auto mapper = vtkSmartPointer<vtkPolyDataMapper>::New();
    auto actor = vtkSmartPointer<vtkActor>::New();

    poly->ComputeBounds();
    double* b = poly->GetBounds();

    if (item.colorMode == ColorMode::Original) {
        auto sc = poly->GetPointData()->GetScalars();
        if (sc && sc->GetNumberOfComponents() == 3) {
            mapper->SetInputConnection(glyph->GetOutputPort());
            mapper->SetScalarVisibility(true);
            mapper->SetColorModeToDirectScalars();
            actor->SetMapper(mapper);
            return actor;
        }
        // fall through to solid if no RGB scalars
    }

    if (item.colorMode == ColorMode::Height) {
        auto elev = vtkSmartPointer<vtkElevationFilter>::New();
        elev->SetInputConnection(glyph->GetOutputPort());
        elev->SetLowPoint(0.0, 0.0, b[4]);
        elev->SetHighPoint(0.0, 0.0, b[5]);
        mapper->SetInputConnection(elev->GetOutputPort());
        mapper->SetScalarVisibility(true);
        mapper->SetColorModeToDefault();
        mapper->SetScalarRange(b[4], b[5]);
        actor->SetMapper(mapper);
        return actor;
    }

    // Solid color
    mapper->SetInputConnection(glyph->GetOutputPort());
    mapper->SetScalarVisibility(false);
    actor->SetMapper(mapper);
    actor->GetProperty()->SetColor(item.displayColor.redF(),
                                   item.displayColor.greenF(),
                                   item.displayColor.blueF());
    return actor;
}

int CloudManager::addCloud(vtkPolyData* poly, const QString& name, const QColor& color)
{
    PointCloudItem item;
    item.name = name;
    item.displayColor = color;
    item.data = poly;
    poly->ComputeBounds();
    poly->GetBounds(item.bounds);

    item.actor = buildActor(poly, item);
    item.actor->SetVisibility(item.visible ? 1 : 0);
    m_renderer->AddActor(item.actor);

    auto outline = vtkSmartPointer<vtkOutlineFilter>::New();
    outline->SetInputData(poly);
    auto boxMapper = vtkSmartPointer<vtkPolyDataMapper>::New();
    boxMapper->SetInputConnection(outline->GetOutputPort());
    auto boxActor = vtkSmartPointer<vtkActor>::New();
    boxActor->SetMapper(boxMapper);
    boxActor->GetProperty()->SetColor(1.0, 1.0, 1.0);
    boxActor->SetVisibility(0);
    m_renderer->AddActor(boxActor);
    item.boxActor = boxActor;

    m_items.push_back(item);
    emit changed();
    return static_cast<int>(m_items.size()) - 1;
}

void CloudManager::removeCloud(int index)
{
    if (index < 0 || index >= static_cast<int>(m_items.size())) return;
    auto& it = m_items[index];
    if (it.actor)    m_renderer->RemoveActor(it.actor);
    if (it.boxActor) m_renderer->RemoveActor(it.boxActor);
    m_items.erase(m_items.begin() + index);
    emit changed();
}

void CloudManager::clear()
{
    for (auto& it : m_items) {
        if (it.actor)    m_renderer->RemoveActor(it.actor);
        if (it.boxActor) m_renderer->RemoveActor(it.boxActor);
    }
    m_items.clear();
    emit changed();
}

void CloudManager::setVisible(int index, bool visible)
{
    if (index < 0 || index >= static_cast<int>(m_items.size())) return;
    auto& it = m_items[index];
    it.visible = visible;
    if (it.actor) it.actor->SetVisibility(visible ? 1 : 0);
}

void CloudManager::setColor(int index, const QColor& color)
{
    if (index < 0 || index >= static_cast<int>(m_items.size())) return;
    auto& it = m_items[index];
    it.displayColor = color;
    if (it.colorMode == ColorMode::Solid && it.actor)
        it.actor->GetProperty()->SetColor(color.redF(), color.greenF(), color.blueF());
}

void CloudManager::setColorMode(int index, ColorMode mode)
{
    if (index < 0 || index >= static_cast<int>(m_items.size())) return;
    auto& it = m_items[index];
    it.colorMode = mode;
    m_renderer->RemoveActor(it.actor);
    it.actor = buildActor(it.data, it);
    it.actor->SetVisibility(it.visible ? 1 : 0);
    m_renderer->AddActor(it.actor);
    emit changed();
}

void CloudManager::setOpacity(int index, double opacity)
{
    if (index < 0 || index >= static_cast<int>(m_items.size())) return;
    if (m_items[index].actor)
        m_items[index].actor->GetProperty()->SetOpacity(opacity);
}

void CloudManager::setBackground(const QColor& color)
{
    m_renderer->SetBackground(color.redF(), color.greenF(), color.blueF());
    if (m_renderer->GetRenderWindow()) m_renderer->GetRenderWindow()->Render();
}

void CloudManager::setAxesVisible(bool visible)
{
    m_axes->SetVisibility(visible ? 1 : 0);
    m_axesVisible = visible;
    if (m_renderer->GetRenderWindow()) m_renderer->GetRenderWindow()->Render();
}

void CloudManager::setBoundingBoxVisible(int index, bool visible)
{
    if (index < 0 || index >= static_cast<int>(m_items.size())) return;
    auto& it = m_items[index];
    if (it.boxActor) it.boxActor->SetVisibility(visible ? 1 : 0);
}

void CloudManager::frameScene()
{
    m_renderer->ResetCamera();
    if (m_renderer->GetRenderWindow())
        m_renderer->GetRenderWindow()->Render();
}
