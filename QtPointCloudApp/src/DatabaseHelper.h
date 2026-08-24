#pragma once

#include <QString>
#include <QVector>

struct SavedCloudRecord
{
    int id = 0;
    QString name;
    double volumeVoxel = 0.0;
    double volumeConvex = 0.0;
    QString createdAt;
};

// SQLite-backed persistence using Qt's QSQLITE driver (no external deps).
class DatabaseHelper
{
public:
    static bool init(const QString& dbPath = "clouds.db");
    static bool saveCloud(const QString& name, const QByteArray& plyData,
                          double volVoxel, double volConvex);
    static QVector<SavedCloudRecord> listClouds();
    static QByteArray loadPly(int id);
    static bool deleteCloud(int id);

private:
    static QString s_dbPath;
};
