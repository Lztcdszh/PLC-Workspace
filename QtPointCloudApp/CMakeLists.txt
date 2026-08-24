cmake_minimum_required(VERSION 3.16)
project(QtPointCloudApp LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)
set(CMAKE_AUTORCC ON)
set(CMAKE_AUTOUIC ON)

# ---- Dependencies ----
find_package(Qt6 REQUIRED COMPONENTS Widgets OpenGLWidgets Sql)
find_package(VTK REQUIRED)

# MQTT is optional: enabled only if Qt6::Mqtt component is available.
find_package(Qt6 COMPONENTS Mqtt QUIET)
if(Qt6Mqtt_FOUND)
  message(STATUS "Qt6::Mqtt found - MQTT publishing enabled")
  add_compile_definitions(HAS_QT_MQTT)
endif()

# ---- Target ----
add_executable(QtPointCloudApp
  src/main.cpp
  src/MainWindow.h src/MainWindow.cpp
  src/VtkView.h src/VtkView.cpp
  src/PointCloudItem.h
  src/CloudManager.h src/CloudManager.cpp
  src/GeometryHelper.h src/GeometryHelper.cpp
  src/PcdParser.h src/PcdParser.cpp
  src/Exporter.h src/Exporter.cpp
  src/MqttClient.h src/MqttClient.cpp
  src/DatabaseHelper.h src/DatabaseHelper.cpp
)

set_target_properties(QtPointCloudApp PROPERTIES
  AUTOMOC ON
  AUTORCC ON
  AUTOUIC ON
)

target_include_directories(QtPointCloudApp PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)

target_link_libraries(QtPointCloudApp PRIVATE
  Qt6::Widgets
  Qt6::Sql
  ${VTK_LIBRARIES}
)
if(Qt6Mqtt_FOUND)
  target_link_libraries(QtPointCloudApp PRIVATE Qt6::Mqtt)
endif()

# Required when linking VTK libraries that use module auto-init.
vtk_module_autoinit(TARGETS QtPointCloudApp MODULES ${VTK_LIBRARIES})
