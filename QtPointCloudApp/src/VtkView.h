#pragma once

#include <QWidget>
#include <vtkSmartPointer.h>
#include <vtkRenderWindow.h>

// A QWidget that hosts a native VTK (Win32 OpenGL) render window by embedding
// its HWND as a child of this widget. This avoids needing VTK built with Qt
// support (i.e. no QVTKOpenGLNativeWidget dependency).
class VtkView : public QWidget
{
    Q_OBJECT
public:
    explicit VtkView(QWidget* parent = nullptr);
    ~VtkView() override;

    vtkRenderWindow* renderWindow() const { return m_win; }

signals:
    void windowReady(vtkRenderWindow* win);

protected:
    void showEvent(QShowEvent* event) override;
    void resizeEvent(QResizeEvent* event) override;

private:
    void ensureInitialized();
    void syncSize();

    vtkSmartPointer<vtkRenderWindow> m_win;
    bool m_initialized = false;
};
