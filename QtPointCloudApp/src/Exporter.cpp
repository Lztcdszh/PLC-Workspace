#include "Exporter.h"

#include <vtkWindowToImageFilter.h>
#include <vtkPNGWriter.h>
#include <vtkJPEGWriter.h>
#include <vtkBMPWriter.h>
#include <vtkSmartPointer.h>

bool Exporter::exportImage(vtkRenderWindow* win, const QString& filePath)
{
    if (!win) return false;

    auto toImg = vtkSmartPointer<vtkWindowToImageFilter>::New();
    toImg->SetInput(win);
    toImg->SetScale(1);
    toImg->ReadFrontBufferOff();
    toImg->Update();

    QString ext = filePath.toLower();
    if (ext.endsWith(".png")) {
        auto w = vtkSmartPointer<vtkPNGWriter>::New();
        w->SetInputConnection(toImg->GetOutputPort());
        w->SetFileName(filePath.toUtf8().constData());
        w->Write();
        return w->GetErrorCode() == 0;
    } else if (ext.endsWith(".jpg") || ext.endsWith(".jpeg")) {
        auto w = vtkSmartPointer<vtkJPEGWriter>::New();
        w->SetInputConnection(toImg->GetOutputPort());
        w->SetFileName(filePath.toUtf8().constData());
        w->Write();
        return w->GetErrorCode() == 0;
    } else {
        auto w = vtkSmartPointer<vtkBMPWriter>::New();
        w->SetInputConnection(toImg->GetOutputPort());
        w->SetFileName(filePath.toUtf8().constData());
        w->Write();
        return w->GetErrorCode() == 0;
    }
}
