#include "MqttClient.h"

#ifdef HAS_QT_MQTT
#include <QMqttTopicName>
#endif

MqttClient::MqttClient(QObject* parent) : QObject(parent)
{
#ifdef HAS_QT_MQTT
    m_client = new QMqttClient(this);
    m_client->setHostname("localhost");
    m_client->setPort(1883);
    QObject::connect(m_client, &QMqttClient::errorChanged, this, [this](QMqttClient::ClientError e) {
        m_error = QString("MQTT error: %1").arg(e);
    });
#endif
}

bool MqttClient::connectTo(const QString& broker, quint16 port)
{
#ifdef HAS_QT_MQTT
    if (!m_client) return false;
    m_client->setHostname(broker);
    m_client->setPort(port);
    m_client->connectToHost();
    return m_client->state() == QMqttClient::Connected;
#else
    m_error = "MQTT support not compiled (Qt6::Mqtt missing)";
    return false;
#endif
}

void MqttClient::publish(const QString& topic, const QByteArray& payload)
{
#ifdef HAS_QT_MQTT
    if (!m_client || m_client->state() != QMqttClient::Connected) {
        m_error = "Not connected to broker";
        return;
    }
    m_client->publish(QMqttTopicName(topic), payload);
#else
    m_error = "MQTT support not compiled (Qt6::Mqtt missing)";
#endif
}

bool MqttClient::isConnected() const
{
#ifdef HAS_QT_MQTT
    return m_client && m_client->state() == QMqttClient::Connected;
#else
    return false;
#endif
}
