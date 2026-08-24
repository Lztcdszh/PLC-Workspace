# 本地构建 + 测试脚本（Windows PowerShell）
# 用法：在仓库根目录执行  .\build-test.ps1
$ErrorActionPreference = "Stop"

Write-Host "==> 还原解决方案" -ForegroundColor Cyan
dotnet restore Workspace.sln

Write-Host "==> 构建解决方案 (Release)" -ForegroundColor Cyan
dotnet build Workspace.sln -c Release --no-restore

Write-Host "==> 运行基础库单元测试" -ForegroundColor Cyan
dotnet test Tests/PlcFoundationTests/PlcFoundationTests.csproj -c Release --no-build

Write-Host "==> 完成" -ForegroundColor Green
