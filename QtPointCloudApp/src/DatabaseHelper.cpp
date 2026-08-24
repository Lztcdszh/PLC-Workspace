#include "DatabaseHelper.h"

#include <QSqlDatabase>
#include <QSqlQuery>
#include <QSqlError>
#include <QVariant>
#include <QDateTime>

QString DatabaseHelper::s_dbPath = "clouds.db";

bool DatabaseHelper::init(const QString& dbPath)
{
    s_dbPath = dbPath;
    QSqlDatabase db = QSqlDatabase::addDatabase("QSQLITE", "clouds");
    db.setDatabaseName(s_dbPath);
    if (!db.open()) return false;

    QSqlQuery q(db);
    return q.exec(
        "CREATE TABLE IF NOT EXISTS clouds ("
        "  id INTEGER PRIMARY KEY AUTOINCREMENT,"
        "  name TEXT,"
        "  volume_voxel REAL,"
        "  volume_convex REAL,"
        "  ply BLOB,"
        "  created_at TEXT)");
}

bool DatabaseHelper::saveCloud(const QString& name, const QByteArray& plyData,
                               double volVoxel, double volConvex)
{
    QSqlDatabase db = QSqlDatabase::database("clouds");
    if (!db.isOpen() && !db.open()) return false;

    QSqlQuery q(db);
    q.prepare("INSERT INTO clouds (name, volume_voxel, volume_convex, ply, created_at) "
              "VALUES (?, ?, ?, ?, ?)");
    q.addBindValue(name);
    q.addBindValue(volVoxel);
    q.addBindValue(volConvex);
    q.addBindValue(plyData);
    q.addBindValue(QDateTime::currentDateTime().toString(Qt::ISODate));
    return q.exec();
}

QVector<SavedCloudRecord> DatabaseHelper::listClouds()
{
    QVector<SavedCloudRecord> out;
    QSqlDatabase db = QSqlDatabase::database("clouds");
    if (!db.isOpen() && !db.open()) return out;

    QSqlQuery q("SELECT id, name, volume_voxel, volume_convex, created_at FROM clouds ORDER BY id DESC", db);
    while (q.next()) {
        SavedCloudRecord r;
        r.id = q.value(0).toInt();
        r.name = q.value(1).toString();
        r.volumeVoxel = q.value(2).toDouble();
        r.volumeConvex = q.value(3).toDouble();
        r.createdAt = q.value(4).toString();
        out.append(r);
    }
    return out;
}

QByteArray DatabaseHelper::loadPly(int id)
{
    QSqlDatabase db = QSqlDatabase::database("clouds");
    if (!db.isOpen() && !db.open()) return QByteArray();

    QSqlQuery q(db);
    q.prepare("SELECT ply FROM clouds WHERE id = ?");
    q.addBindValue(id);
    if (q.exec() && q.next())
        return q.value(0).toByteArray();
    return QByteArray();
}

bool DatabaseHelper::deleteCloud(int id)
{
    QSqlDatabase db = QSqlDatabase::database("clouds");
    if (!db.isOpen() && !db.open()) return false;

    QSqlQuery q(db);
    q.prepare("DELETE FROM clouds WHERE id = ?");
    q.addBindValue(id);
    return q.exec();
}
