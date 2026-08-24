#include "MainWindow.h"

#include <QMenuBar>
#include <QStatusBar>
#include <QToolBar>
#include <QDockWidget>
#include <QListWidget>
#include <QVBoxLayout>
#include <QHBoxLayout>
#include <QPushButton>
#include <QCheckBox>
#include <QComboBox>
#include <QLabel>
#include <QMessageBox>
#include <QFileDialog>
#include <QInputDialog>
#include <QColorDialog>
#include <QTimer>
#include <QJsonDocument>
#include <QJsonObject>
#include <QJsonArray>
#include <QDir>
#include <QDateTime>

#include <vtkPolyData.h>
#include <vtkPoints.h>
#include <vtkVertexGlyphFilter.h>
#include <vtkPolyDataMapper.h>
#include <vtkPLYReader.h>
#include <vtkPLYWriter.h>
#include <vtkActor.h>
#include <vtkProperty.h>

#include "PcdParser.h"
#include "GeometryHelper.h"
#include "Exporter.h"
#include "MqttClient.h"
#include "DatabaseHelper.h"

namespace {

// ---- Sample point-cloud generators ----
vtkSmartPointer<vtkPolyData> genSphere(int n = 2000, double r = 1.0)
{
    auto pts = vtkSmartPointer<vtkPoints>::New();
    for (int i = 0; i < n; ++i) {
        double u = (double)rand() / RAND_MAX * 2.0 - 1.0;
        double th = (double)rand() / RAND_MAX * 2.0 * M_PI;
        double s = sqrt(1.0 - u * u);
        pts->InsertNextPoint(r * s * cos(th), r * s * sin(th), r * u);
    }
    auto poly = vtkSmartPointer<vtkPolyData>::New();
    poly->SetPoints(pts);
    return poly;
}

vtkSmartPointer<vtkPolyData> genCube(int n = 2000, double s = 1.0)
{
    auto pts = vtkSmartPointer<vtkPoints>::New();
    for (int i = 0; i < n; ++i) {
        pts->InsertNextPoint((double)rand() / RAND_MAX * s - s / 2,
                             (double)rand() / RAND_MAX * s - s / 2,
                             (double)rand() / RAND_MAX * s - s / 2);
    }
    auto poly = vtkSmartPointer<vtkPolyData>::New();
    poly->SetPoints(pts);
    return poly;
}

vtkSmartPointer<vtkPolyData> genSpiral(int n = 2000)
{
    auto pts = vtkSmartPointer<vtkPoints>::New();
    for (int i = 0; i < n; ++i) {
        double t = (double)i / n * 20.0 * M_PI;
        double rad = 0.02 * t;
        pts->InsertNextPoint(rad * cos(t), rad * sin(t), 0.05 * t);
    }
    auto poly = vtkSmartPointer<vtkPolyData>::New();
    poly->SetPoints(pts);
    return poly;
}

vtkSmartPointer<vtkPolyData> genPlane(int n = 2000, double s = 2.0)
{
    auto pts = vtkSmartPointer<vtkPoints>::New();
    int side = (int)sqrt((double)n);
    for (int i = 0; i < side; ++i)
        for (int j = 0; j < side; ++j)
            pts->InsertNextPoint((double)i / side * s - s / 2,
                                 (double)j / side * s - s / 2,
                                 0.0);
    auto poly = vtkSmartPointer<vtkPolyData>::New();
    poly->SetPoints(pts);
    return poly;
}

} // namespace

MainWindow::MainWindow(QWidget* parent)
    : QMainWindow(parent)
{
    setWindowTitle("Qt Point Cloud Viewer (WpfApp1 port)");
    resize(1100, 700);

    m_vtk = new VtkView(this);
    setCentralWidget(m_vtk);

    m_cloudMgr = new CloudManager(this);
    m_cloudMgr->setRenderWindow(m_vtk->renderWindow());
    connect(m_cloudMgr, &CloudManager::changed, this, &MainWindow::refreshList);
    connect(m_cloudMgr, &CloudManager::changed, this, &MainWindow::renderNow);

    m_mqtt = new MqttClient(this);

    // ---- Left dock: cloud list + controls ----
    auto* dock = new QDockWidget("Point Clouds", this);
    auto* panel = new QWidget(dock);
    auto* vlay = new QVBoxLayout(panel);

    m_list = new QListWidget(panel);
    connect(m_list, &QListWidget::currentRowChanged, this, &MainWindow::onSelectionChanged);
    vlay->addWidget(m_list);

    m_colorModeCombo = new QComboBox(panel);
    m_colorModeCombo->addItems({"Original", "Height", "Solid"});
    connect(m_colorModeCombo, QOverload<int>::of(&QComboBox::currentIndexChanged),
            this, &MainWindow::onColorModeChanged);
    vlay->addWidget(new QLabel("Color mode:"));
    vlay->addWidget(m_colorModeCombo);

    auto* btnRow1 = new QHBoxLayout();
    m_colorBtn = new QPushButton("Color");
    m_removeBtn = new QPushButton("Remove");
    btnRow1->addWidget(m_colorBtn);
    btnRow1->addWidget(m_removeBtn);
    vlay->addLayout(btnRow1);
    connect(m_colorBtn, &QPushButton::clicked, this, &MainWindow::onPickColor);
    connect(m_removeBtn, &QPushButton::clicked, this, &MainWindow::onRemoveCloud);

    auto* btnRow2 = new QHBoxLayout();
    m_visibleChk = new QCheckBox("Visible");
    m_bboxChk = new QCheckBox("Bounding box");
    m_visibleChk->setChecked(true);
    btnRow2->addWidget(m_visibleChk);
    btnRow2->addWidget(m_bboxChk);
    vlay->addLayout(btnRow2);
    connect(m_visibleChk, &QCheckBox::toggled, this, &MainWindow::onToggleVisible);
    connect(m_bboxChk, &QCheckBox::toggled, this, &MainWindow::onToggleBBox);

    vlay->addStretch(1);
    dock->setWidget(panel);
    addDockWidget(Qt::LeftDockWidgetArea, dock);

    // ---- Status bar ----
    m_status = new QLabel("Ready");
    statusBar()->addWidget(m_status);

    // ---- Menus ----
    auto* fileMenu = menuBar()->addMenu("File");
    fileMenu->addAction("Import PLY...", this, &MainWindow::onImportPly);
    fileMenu->addAction("Import PCD...", this, &MainWindow::onImportPcd);
    fileMenu->addAction("Export PNG...", this, &MainWindow::onExportPng);
    fileMenu->addSeparator();
    fileMenu->addAction("Save to DB", this, &MainWindow::onSaveDb);
    fileMenu->addAction("Load from DB...", this, &MainWindow::onLoadDb);
    fileMenu->addSeparator();
    fileMenu->addAction("Exit", this, &QWidget::close);

    auto* sampleMenu = menuBar()->addMenu("Samples");
    sampleMenu->addAction("Sphere", this, &MainWindow::onSampleSphere);
    sampleMenu->addAction("Cube", this, &MainWindow::onSampleCube);
    sampleMenu->addAction("Spiral", this, &MainWindow::onSampleSpiral);
    sampleMenu->addAction("Plane", this, &MainWindow::onSamplePlane);

    auto* viewMenu = menuBar()->addMenu("View");
    auto* axesAct = viewMenu->addAction("Axes");
    axesAct->setCheckable(true);
    axesAct->setChecked(true);
    connect(axesAct, &QAction::toggled, this, &MainWindow::onToggleAxes);
    viewMenu->addAction("Background color...", this, &MainWindow::onBackground);
    viewMenu->addAction("Frame scene", this, &MainWindow::onFrame);

    auto* toolMenu = menuBar()->addMenu("Tools");
    toolMenu->addAction("Voxel volume", this, &MainWindow::onVoxelVolume);
    toolMenu->addAction("Convex-hull volume", this, &MainWindow::onConvexVolume);
    toolMenu->addSeparator();
    toolMenu->addAction("Clip box...", this, &MainWindow::onClipBox);
    toolMenu->addAction("Clip plane...", this, &MainWindow::onClipPlane);
    toolMenu->addAction("Slice play", this, &MainWindow::onSlicePlay);

    auto* mqttMenu = menuBar()->addMenu("MQTT");
    mqttMenu->addAction("Connect...", this, &MainWindow::onMqttConnect);
    mqttMenu->addAction("Publish status", this, &MainWindow::onMqttPublish);
}

MainWindow::~MainWindow()
{
    clearSlices();
}

// ---- Sample / import helpers ----
void MainWindow::addSample(const QString& name, vtkSmartPointer<vtkPolyData> poly, const QColor& c)
{
    m_cloudMgr->addCloud(poly, name, c);
    m_cloudMgr->frameScene();
    m_status->setText(QString("Added %1 (%2 pts)").arg(name).arg(poly->GetNumberOfPoints()));
}

void MainWindow::onSampleSphere() { addSample("Sphere", genSphere(), nextPaletteColor()); }
void MainWindow::onSampleCube()   { addSample("Cube", genCube(), nextPaletteColor()); }
void MainWindow::onSampleSpiral() { addSample("Spiral", genSpiral(), nextPaletteColor()); }
void MainWindow::onSamplePlane()  { addSample("Plane", genPlane(), nextPaletteColor()); }

void MainWindow::onImportPly()
{
    QString path = QFileDialog::getOpenFileName(this, "Open PLY", "", "PLY (*.ply)");
    if (path.isEmpty()) return;
    auto reader = vtkSmartPointer<vtkPLYReader>::New();
    reader->SetFileName(path.toUtf8().constData());
    reader->Update();
    auto poly = vtkSmartPointer<vtkPolyData>::New();
    poly->ShallowCopy(reader->GetOutput());
    if (!poly->GetPoints()) { QMessageBox::warning(this, "Error", "Failed to load PLY"); return; }
    addSample(QFileInfo(path).fileName(), poly, nextPaletteColor());
}

void MainWindow::onImportPcd()
{
    QString path = QFileDialog::getOpenFileName(this, "Open PCD", "", "PCD (*.pcd)");
    if (path.isEmpty()) return;
    auto poly = PcdParser::parse(path);
    if (!poly || !poly->GetPoints()) { QMessageBox::warning(this, "Error", "Failed to parse PCD"); return; }
    addSample(QFileInfo(path).fileName(), poly, nextPaletteColor());
}

void MainWindow::onExportPng()
{
    QString path = QFileDialog::getSaveFileName(this, "Export image", "", "PNG (*.png);;JPEG (*.jpg);;BMP (*.bmp)");
    if (path.isEmpty()) return;
    if (Exporter::exportImage(m_vtk->renderWindow(), path))
        m_status->setText("Exported " + path);
    else
        QMessageBox::warning(this, "Error", "Export failed");
}

// ---- View ----
void MainWindow::onToggleAxes(bool on) { m_cloudMgr->setAxesVisible(on); }
void MainWindow::onFrame() { m_cloudMgr->frameScene(); }

void MainWindow::onBackground()
{
    QColor c = QColorDialog::getColor(Qt::darkGray, this, "Background");
    if (c.isValid()) m_cloudMgr->setBackground(c);
}

// ---- Selection / list ----
void MainWindow::refreshList()
{
    int sel = m_list->currentRow();
    m_list->blockSignals(true);
    m_list->clear();
    for (const auto& it : m_cloudMgr->items())
        m_list->addItem(QString("%1  [%2]").arg(it.name).arg(
            it.colorMode == ColorMode::Original ? "orig" :
            it.colorMode == ColorMode::Height  ? "height" : "solid"));
    m_list->blockSignals(false);
    if (sel >= 0 && sel < m_list->count()) m_list->setCurrentRow(sel);
}

void MainWindow::onSelectionChanged()
{
    int i = m_list->currentRow();
    bool ok = i >= 0 && i < m_cloudMgr->count();
    m_colorModeCombo->setEnabled(ok);
    m_colorBtn->setEnabled(ok);
    m_removeBtn->setEnabled(ok);
    m_visibleChk->setEnabled(ok);
    m_bboxChk->setEnabled(ok);
    if (ok) {
        const auto& it = m_cloudMgr->items()[i];
        m_colorModeCombo->setCurrentIndex((int)it.colorMode);
        m_visibleChk->setChecked(it.visible);
        m_bboxChk->setChecked(it.boxActor && it.boxActor->GetVisibility());
    }
}

void MainWindow::onRemoveCloud()
{
    int i = m_list->currentRow();
    if (i < 0) return;
    m_cloudMgr->removeCloud(i);
    renderNow();
}

void MainWindow::onColorModeChanged(int idx)
{
    int i = m_list->currentRow();
    if (i < 0) return;
    m_cloudMgr->setColorMode(i, (ColorMode)idx);
    renderNow();
}

void MainWindow::onPickColor()
{
    int i = m_list->currentRow();
    if (i < 0) return;
    QColor c = QColorDialog::getColor(m_cloudMgr->items()[i].displayColor, this, "Cloud color");
    if (c.isValid()) { m_cloudMgr->setColor(i, c); renderNow(); }
}

void MainWindow::onToggleVisible(bool on)
{
    int i = m_list->currentRow();
    if (i < 0) return;
    m_cloudMgr->setVisible(i, on);
    renderNow();
}

void MainWindow::onToggleBBox(bool on)
{
    int i = m_list->currentRow();
    if (i < 0) return;
    m_cloudMgr->setBoundingBoxVisible(i, on);
    renderNow();
}

// ---- Geometry tools ----
void MainWindow::onVoxelVolume()
{
    int i = m_list->currentRow();
    if (i < 0) { QMessageBox::information(this, "Volume", "Select a cloud first"); return; }
    double v = GeometryHelper::computeVoxelVolume(m_cloudMgr->items()[i].data);
    QMessageBox::information(this, "Voxel volume",
        QString("Occupied-voxel volume: %1").arg(v, 0, 'f', 6));
}

void MainWindow::onConvexVolume()
{
    int i = m_list->currentRow();
    if (i < 0) { QMessageBox::information(this, "Volume", "Select a cloud first"); return; }
    double v = GeometryHelper::computeConvexHullVolume(m_cloudMgr->items()[i].data);
    QMessageBox::information(this, "Convex volume",
        QString("Convex-hull volume: %1").arg(v, 0, 'f', 6));
}

void MainWindow::onClipBox()
{
    int i = m_list->currentRow();
    if (i < 0) { QMessageBox::information(this, "Clip", "Select a cloud first"); return; }
    bool ok;
    QString s = QInputDialog::getText(this, "Clip box",
        "minX,minY,minZ,maxX,maxY,maxZ", QLineEdit::Normal, "-0.5,-0.5,-0.5,0.5,0.5,0.5", &ok);
    if (!ok) return;
    QStringList p = s.split(',');
    if (p.size() != 6) return;
    double mn[3], mx[3];
    for (int k = 0; k < 3; ++k) { mn[k] = p[k].toDouble(); mx[k] = p[k + 3].toDouble(); }
    auto clipped = GeometryHelper::clipBox(m_cloudMgr->items()[i].data, mn, mx, false);
    m_cloudMgr->addCloud(clipped, "ClippedBox", nextPaletteColor());
    m_cloudMgr->frameScene();
}

void MainWindow::onClipPlane()
{
    int i = m_list->currentRow();
    if (i < 0) { QMessageBox::information(this, "Clip", "Select a cloud first"); return; }
    bool ok;
    QString s = QInputDialog::getText(this, "Clip plane",
        "nx,ny,nz,ox,oy,oz", QLineEdit::Normal, "0,0,1,0,0,0", &ok);
    if (!ok) return;
    QStringList p = s.split(',');
    if (p.size() != 6) return;
    double n[3], o[3];
    for (int k = 0; k < 3; ++k) { n[k] = p[k].toDouble(); o[k] = p[k + 3].toDouble(); }
    auto clipped = GeometryHelper::clipPlane(m_cloudMgr->items()[i].data, n, o, false);
    m_cloudMgr->addCloud(clipped, "ClippedPlane", nextPaletteColor());
    m_cloudMgr->frameScene();
}

// ---- Slicing animation ----
void MainWindow::clearSlices()
{
    if (m_sliceTimer) { m_sliceTimer->stop(); delete m_sliceTimer; m_sliceTimer = nullptr; }
    auto* ren = m_cloudMgr->renderer();
    for (auto& a : m_sliceActors)
        if (ren && a) ren->RemoveActor(a);
    m_sliceActors.clear();
    m_sliceIdx = 0;
}

void MainWindow::onSlicePlay()
{
    int i = m_list->currentRow();
    if (i < 0) { QMessageBox::information(this, "Slice", "Select a cloud first"); return; }
    clearSlices();

    auto data = m_cloudMgr->items()[i].data;
    data->ComputeBounds();
    double* b = data->GetBounds();
    const int N = 24;
    double z0 = b[4], z1 = b[5];
    double step = (z1 - z0) / N;

    auto ren = m_cloudMgr->renderer();
    for (int s = 0; s < N; ++s) {
        double lo = z0 + s * step;
        double hi = lo + step;
        auto pts = vtkSmartPointer<vtkPoints>::New();
        auto src = data->GetPoints();
        for (vtkIdType p = 0; p < src->GetNumberOfPoints(); ++p) {
            double q[3]; src->GetPoint(p, q);
            if (q[2] >= lo && q[2] < hi) pts->InsertNextPoint(q);
        }
        auto sub = vtkSmartPointer<vtkPolyData>::New();
        sub->SetPoints(pts);
        auto glyph = vtkSmartPointer<vtkVertexGlyphFilter>::New();
        glyph->SetInputData(sub); glyph->Update();
        auto mapper = vtkSmartPointer<vtkPolyDataMapper>::New();
        mapper->SetInputConnection(glyph->GetOutputPort());
        auto actor = vtkSmartPointer<vtkActor>::New();
        actor->SetMapper(mapper);
        actor->GetProperty()->SetColor(0.2, 0.8, 1.0);
        actor->SetVisibility(0);
        ren->AddActor(actor);
        m_sliceActors.append(actor);
    }

    m_sliceIdx = 0;
    m_sliceTimer = new QTimer(this);
    connect(m_sliceTimer, &QTimer::timeout, this, &MainWindow::onSliceTick);
    m_sliceTimer->start(120);
}

void MainWindow::onSliceTick()
{
    if (m_sliceIdx >= m_sliceActors.size()) {
        if (m_sliceTimer) { m_sliceTimer->stop(); delete m_sliceTimer; m_sliceTimer = nullptr; }
        return;
    }
    m_sliceActors[m_sliceIdx]->SetVisibility(1);
    ++m_sliceIdx;
    renderNow();
}

// ---- MQTT ----
void MainWindow::onMqttConnect()
{
    bool ok;
    QString broker = QInputDialog::getText(this, "MQTT broker",
        "broker:port", QLineEdit::Normal, "localhost:1883", &ok);
    if (!ok) return;
    QStringList hp = broker.split(':');
    quint16 port = hp.size() == 2 ? hp[1].toUShort() : 1883;
    if (m_mqtt->connectTo(hp[0], port))
        m_status->setText("MQTT connected");
    else
        QMessageBox::warning(this, "MQTT", m_mqtt->lastError());
}

void MainWindow::onMqttPublish()
{
    QJsonArray arr;
    for (const auto& it : m_cloudMgr->items()) {
        QJsonObject o;
        o["name"] = it.name;
        o["points"] = (int)it.data->GetNumberOfPoints();
        o["voxelVolume"] = GeometryHelper::computeVoxelVolume(it.data);
        o["convexVolume"] = GeometryHelper::computeConvexHullVolume(it.data);
        o["bbox"] = QJsonArray{ it.bounds[0], it.bounds[1], it.bounds[2],
                                it.bounds[3], it.bounds[4], it.bounds[5] };
        arr.append(o);
    }
    QJsonObject root; root["clouds"] = arr;
    root["timestamp"] = QDateTime::currentDateTime().toString(Qt::ISODate);
    QByteArray payload = QJsonDocument(root).toJson();

    if (m_mqtt->isConnected())
        m_mqtt->publish("pointcloud/status", payload);
    else
        QMessageBox::warning(this, "MQTT", "Not connected. Use MQTT > Connect first.");
}

// ---- Database ----
QByteArray MainWindow::writePlyToBytes(vtkPolyData* poly)
{
    QString tmp = QDir::tempPath() + "/_tmp_cloud_export.ply";
    auto w = vtkSmartPointer<vtkPLYWriter>::New();
    w->SetInputData(poly);
    w->SetFileName(tmp.toUtf8().constData());
    w->Write();
    QFile f(tmp);
    if (!f.open(QIODevice::ReadOnly)) return QByteArray();
    return f.readAll();
}

void MainWindow::onSaveDb()
{
    int i = m_list->currentRow();
    if (i < 0) { QMessageBox::information(this, "DB", "Select a cloud first"); return; }
    const auto& it = m_cloudMgr->items()[i];
    QByteArray ply = writePlyToBytes(it.data);
    double vv = GeometryHelper::computeVoxelVolume(it.data);
    double cv = GeometryHelper::computeConvexHullVolume(it.data);
    if (DatabaseHelper::saveCloud(it.name, ply, vv, cv))
        m_status->setText("Saved to database");
    else
        QMessageBox::warning(this, "DB", "Save failed (is clouds.db initialized?)");
}

void MainWindow::onLoadDb()
{
    auto recs = DatabaseHelper::listClouds();
    if (recs.isEmpty()) { QMessageBox::information(this, "DB", "No saved clouds"); return; }
    QStringList items;
    for (const auto& r : recs)
        items << QString("#%1  %2  Vv=%3 Vc=%4  %5")
                 .arg(r.id).arg(r.name)
                 .arg(r.volumeVoxel, 0, 'f', 4).arg(r.volumeConvex, 0, 'f', 4)
                 .arg(r.createdAt);
    bool ok;
    QString sel = QInputDialog::getItem(this, "Load cloud", "Pick a record:", items, 0, false, &ok);
    if (!ok) return;
    int id = sel.section(' ', 0, 0).mid(1).toInt();
    QByteArray ply = DatabaseHelper::loadPly(id);
    if (ply.isEmpty()) { QMessageBox::warning(this, "DB", "Load failed"); return; }

    QString tmp = QDir::tempPath() + "/_tmp_cloud_load.ply";
    QFile f(tmp); f.open(QIODevice::WriteOnly); f.write(ply); f.close();

    auto reader = vtkSmartPointer<vtkPLYReader>::New();
    reader->SetFileName(tmp.toUtf8().constData());
    reader->Update();
    auto poly = vtkSmartPointer<vtkPolyData>::New();
    poly->ShallowCopy(reader->GetOutput());
    addSample("DB#" + QString::number(id), poly, nextPaletteColor());
}

// ---- Misc ----
void MainWindow::renderNow()
{
    auto* win = m_vtk->renderWindow();
    if (win) win->Render();
}

QColor MainWindow::nextPaletteColor()
{
    static const QColor palette[] = { Qt::cyan, Qt::yellow, Qt::magenta,
        Qt::green, Qt::red, Qt::blue, Qt::white, QColor(255, 165, 0) };
    QColor c = palette[m_colorCounter % 8];
    ++m_colorCounter;
    return c;
}
