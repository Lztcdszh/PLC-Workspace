#pragma once

#include <QMainWindow>
#include <QTimer>
#include <QVector>
#include <QColor>
#include <vtkSmartPointer.h>
#include <vtkActor.h>
#include <vtkPolyData.h>
#include "CloudManager.h"
#include "VtkView.h"

class QListWidget;
class QLabel;
class QComboBox;
class QCheckBox;
class QPushButton;
class MqttClient;

class MainWindow : public QMainWindow
{
    Q_OBJECT
public:
    MainWindow(QWidget* parent = nullptr);
    ~MainWindow();

private slots:
    void onImportPly();
    void onImportPcd();
    void onExportPng();
    void onSampleSphere();
    void onSampleCube();
    void onSampleSpiral();
    void onSamplePlane();
    void onToggleAxes(bool on);
    void onBackground();
    void onFrame();
    void onSelectionChanged();
    void onRemoveCloud();
    void onColorModeChanged(int idx);
    void onPickColor();
    void onToggleVisible(bool on);
    void onToggleBBox(bool on);
    void onVoxelVolume();
    void onConvexVolume();
    void onClipBox();
    void onClipPlane();
    void onSlicePlay();
    void onSliceTick();
    void onMqttConnect();
    void onMqttPublish();
    void onSaveDb();
    void onLoadDb();
    void refreshList();

private:
    void addSample(const QString& name, vtkSmartPointer<vtkPolyData> poly, const QColor& c);
    void clearSlices();
    void renderNow();
    QByteArray writePlyToBytes(vtkPolyData* poly);

    VtkView* m_vtk = nullptr;
    CloudManager* m_cloudMgr = nullptr;
    QListWidget* m_list = nullptr;
    QLabel* m_status = nullptr;
    QComboBox* m_colorModeCombo = nullptr;
    QCheckBox* m_visibleChk = nullptr;
    QCheckBox* m_bboxChk = nullptr;
    QPushButton* m_colorBtn = nullptr;
    QPushButton* m_removeBtn = nullptr;

    MqttClient* m_mqtt = nullptr;

    QTimer* m_sliceTimer = nullptr;
    QVector<vtkSmartPointer<vtkActor>> m_sliceActors;
    int m_sliceIdx = 0;

    int m_colorCounter = 0;
    QColor nextPaletteColor();
};
