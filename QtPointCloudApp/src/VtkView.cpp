#include "VtkView.h"

#include <QShowEvent>
#include <QResizeEvent>
#include <windows.h>
#include <vtkWin32OpenGLRenderWindow.h>

VtkView::VtkView(QWidget* parent)
    : QWidget(parent)
{
    // Force a real native window so we can grab its HWND for VTK.
    setAttribute(Qt::WA_NativeWindow);
    setAttribute(Qt::WA_PaintOnScreen, false);

    // VTK render window (concrete Win32 GL window on Windows).
    auto* win = vtkWin32OpenGLRenderWindow::New();
    win->SetBorders(0);
    win->SetFullScreen(false);
    win->SetSize(width() > 0 ? width() : 640, height() > 0 ? height() : 480);
    m_win = win;
}

VtkView::~VtkView()
{
    // The render window holds the OpenGL context; let it clean up.
    m_win = nullptr;
}

void VtkView::ensureInitialized()
{
    if (m_initialized) return;
    if (!isVisible()) return;

    HWND hwnd = reinterpret_cast<HWND>(winId());
    if (!hwnd) return;

    auto* win = vtkWin32OpenGLRenderWindow::SafeDownCast(m_win);
    if (!win) return;

    win->SetParentId((void*)hwnd);
    m_initialized = true;
    emit windowReady(m_win);
    syncSize();
}

void VtkView::showEvent(QShowEvent* event)
{
    QWidget::showEvent(event);
    ensureInitialized();
}

void VtkView::resizeEvent(QResizeEvent* event)
{
    QWidget::resizeEvent(event);
    syncSize();
}

void VtkView::syncSize()
{
    if (!m_initialized) return;
    m_win->SetSize(width(), height());
    m_win->Render();
}
