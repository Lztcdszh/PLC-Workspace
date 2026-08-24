#pragma once

#include <QString>
#include <QObject>

#ifdef HAS_QT_MQTT
#include <QMqttClient>
#endif

// Thin wrapper around QMqttClient. Compiled only when Qt6::Mqtt is present,
// so the rest of the app builds even without MQTT support.
class MqttClient : public QObject
{
    Q_OBJECT
public:
    explicit MqttClient(QObject* parent = nullptr);
    bool connectTo(const QString& broker, quint16 port = 1883);
    void publish(const QString& topic, const QByteArray& payload);
    bool isConnected() const;
    QString lastError() const { return m_error; }

private:
#ifdef HAS_QT_MQTT
    QMqttClient* m_client = nullptr;
#endif
    QString m_error;
};
