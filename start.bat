@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
title UGES SD Kontrol - Standalone Baslatici

cd /d "%~dp0"

echo ============================================================================
echo   UGES SD KONTROL ^& SAP VA01 SISTEMI ^(STANDALONE - SDK/NODE GEREKTIRMEZ^)
echo ============================================================================
echo.

:: 1) .env Kontrolu
if not exist ".env" (
    if exist ".env.example" (
        copy /y ".env.example" ".env" >nul
        echo [UYARI] .env dosyasi bulunamadi, .env.example kopyalanarak olusturuldu.
        echo         Lutfen .env icindeki PYTHON_EXE ve DB ayarlarini bilgisayariniza gore duzenleyin!
    ) else (
        echo [HATA]  .env dosyasi bulunamadi!
        pause
        exit /b 1
    )
) else (
    echo [OK]    .env dosyasi bulundu.
)

:: .env Degiskenlerini Oku
for /f "usebackq tokens=1,* delims==" %%A in (".env") do (
    set "line=%%A"
    if defined line (
        if not "!line:~0,1!"=="#" (
            set "%%A=%%B"
        )
    )
)

if not defined BACKEND_PORT set "BACKEND_PORT=5100"
if not defined MCP_PORT set "MCP_PORT=5101"
if not defined DB_HOST set "DB_HOST=localhost"
if not defined DB_PORT set "DB_PORT=5432"
if not defined DB_NAME set "DB_NAME=ud_kokpit"
if not defined DB_SCHEMA set "DB_SCHEMA=sd"

for /f "delims=" %%I in ('powershell -NoProfile -Command "(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254*' } | Select-Object -First 1 -ExpandProperty IPAddress)"') do set "LOCAL_IP=%%I"
if not defined LOCAL_IP set "LOCAL_IP=localhost"

echo         Veritabani : !DB_HOST!:!DB_PORT! / !DB_NAME! ^(Sema: !DB_SCHEMA!^)
echo         Web Ekrani : http://localhost:!BACKEND_PORT!
echo         MCP Sunucu : http://!LOCAL_IP!:!MCP_PORT!/sse  ^(veya /mcp — LLM Suite MCP Tools icin^)
echo.

:: 2) Standalone Exe Kontrolu (SD\app\SdKontrol.API.exe)
if not exist "app\SdKontrol.API.exe" (
    echo [UYARI] app\SdKontrol.API.exe paketi bulunamadi. Gelistirici araclari kontrol ediliyor...
    where dotnet >nul 2>&1
    if errorlevel 1 (
        echo [HATA]  Bu bilgisayarda app\SdKontrol.API.exe klasoru eksik!
        echo         Lutfen SD klasorunu 'app' alt klasoru ile birlikte kopyalayin.
        pause
        exit /b 1
    )
    call "%~dp0paketle.bat"
    if errorlevel 1 exit /b 1
) else (
    echo [OK]    Standalone uygulama paketi hazir ^(app\SdKontrol.API.exe - .NET/Node gerektirmez^).
)

:: 3) Python ^& pywin32 Kontrolu (Sadece direkt SAP GUI acma icin opsiyonel)
if defined PYTHON_EXE (
    if exist "!PYTHON_EXE!" (
        for /f "delims=" %%v in ('"!PYTHON_EXE!" --version 2^>^&1') do set "PY_VER=%%v"
        echo [OK]    Python bulundu: !PY_VER!
        "!PYTHON_EXE!" -c "import win32com.client" >nul 2>&1
        if errorlevel 1 (
            echo [UYARI] Python'da 'pywin32' kurulu degil ^(Direkt VA01 acma icin: "!PYTHON_EXE!" -m pip install pywin32^).
            echo         Bunun yerine ekrandan sifresiz '.vbs indir' butonunu dogrudan kullanabilirsiniz.
        ) else (
            echo [OK]    Python pywin32 ^(SAP GUI COM^) hazir.
        )
    ) else (
        echo [UYARI] PYTHON_EXE bu bilgisayarda bulunamadi ^(!PYTHON_EXE!^).
        echo         .env icinden guncelleyebilir veya ekrandaki sifresiz '.vbs indir' modunu kullanabilirsiniz.
    )
)

:: 4) SAP GUI Kontrolu
if defined SAP_LOGON_PATH (
    if exist "!SAP_LOGON_PATH!" (
        echo [OK]    SAP GUI bulundu: !SAP_LOGON_PATH!
    ) else (
        echo [UYARI] SAP GUI varsayilan yolda bulunamadi: !SAP_LOGON_PATH!
    )
)

:: 5) PostgreSQL Erisim Kontrolu
powershell -NoProfile -Command "$t = New-Object Net.Sockets.TcpClient; try { $t.Connect('!DB_HOST!', !DB_PORT!); $t.Close(); exit 0 } catch { exit 1 }"
if errorlevel 1 (
    echo [HATA]  PostgreSQL veritabanina ulasilamiyor ^(!DB_HOST!:!DB_PORT!^)!
    echo         Lutfen .env dosyasindaki DB_HOST / DB_PORT / sifre bilgilerini kontrol edin.
    pause
    exit /b 1
) else (
    echo [OK]    PostgreSQL baglantisi basarili ^(!DB_HOST!:!DB_PORT!^).
)

:: 6) Excel Raporlari Kontrolu
set "XLSX_COUNT=0"
for %%f in ("*.xlsx") do set /a XLSX_COUNT+=1
echo [BILGI] SD klasorunde !XLSX_COUNT! adet Excel ^(.xlsx^) raporu bulundu.

echo.
echo ============================================================================
echo   WEB UYGULAMASI BASLATILIYOR -> http://localhost:!BACKEND_PORT!
echo   BAGIMSIZ MCP SUNUCUSU AKTIF -> http://!LOCAL_IP!:!MCP_PORT!/sse  ^(/mcp^)
echo   ^(Kapatmak icin bu pencereyi kapatabilir veya Ctrl+C yapabilirsiniz^)
echo ============================================================================
echo.

start "" cmd /c "timeout /t 2 /nobreak >nul & start "" http://localhost:!BACKEND_PORT!"

"app\SdKontrol.API.exe"
pause
