@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [1/2] Frontend statik dosyalari derleniyor (backend\wwwroot)...
pushd frontend
call npm run build
if errorlevel 1 ( popd & exit /b 1 )
popd

echo [2/2] .NET 8 Self-Contained Windows x64 Standalone Exe uretiliyor (SD\app)...
dotnet publish backend\SdKontrol.API.csproj -c Release -r win-x64 --self-contained true -p:PublishDir="%~dp0app" --nologo
if errorlevel 1 exit /b 1

echo.
echo [TAMAM] SD\app\SdKontrol.API.exe hazir! SD klasorunu herhangi bir Windows bilgisayara kopyalayip start.bat ile calistirabilirsiniz.
