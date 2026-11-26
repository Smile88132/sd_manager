@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
title UGES SD Kontrol - Baslatici

cd /d "%~dp0"

echo ============================================================================
echo   UGES SD KONTROL VE SAP VA01 HAZIRLIK SISTEMI - ORTAM KONTROLU
echo ============================================================================
echo.

:: 1) .env Kontrolu
if not exist ".env" (
    if exist ".env.example" (
        copy /y ".env.example" ".env" >nul
        echo [UYARI] .env dosyasi bulunamadi, .env.example kopyalanarak olusturuldu.
        echo         Lutfen .env icindeki PYTHON_EXE ve DB ayarlarini kontrol edin!
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
if not defined FRONTEND_PORT set "FRONTEND_PORT=3100"
if not defined DB_HOST set "DB_HOST=localhost"
if not defined DB_PORT set "DB_PORT=5432"
if not defined DB_NAME set "DB_NAME=ud_kokpit"
if not defined DB_SCHEMA set "DB_SCHEMA=sd"

echo         Veritabani : !DB_HOST!:!DB_PORT! / !DB_NAME! (Sema: !DB_SCHEMA!)
echo         Portlar    : Backend=!BACKEND_PORT! ^| Frontend=!FRONTEND_PORT!
echo.

:: 2) .NET SDK Kontrolu
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [HATA]  .NET SDK bulunamadi! Lutfen .NET 8 SDK yukleyin.
    pause
    exit /b 1
)
for /f "delims=" %%v in ('dotnet --version') do set "DOTNET_VER=%%v"
echo [OK]    .NET SDK hazir: v!DOTNET_VER!

:: 3) Node.js Kontrolu
where node >nul 2>&1
if errorlevel 1 (
    echo [HATA]  Node.js bulunamadi! Lutfen Node.js yukleyin.
    pause
    exit /b 1
)
for /f "delims=" %%v in ('node --version') do set "NODE_VER=%%v"
echo [OK]    Node.js hazir : !NODE_VER!

:: 4) Python & pywin32 Kontrolu
if defined PYTHON_EXE (
    if exist "!PYTHON_EXE!" (
        for /f "delims=" %%v in ('"!PYTHON_EXE!" --version 2^>^&1') do set "PY_VER=%%v"
        echo [OK]    Python bulundu: !PY_VER! ^(!PYTHON_EXE!^)
        "!PYTHON_EXE!" -c "import win32com.client" >nul 2>&1
        if errorlevel 1 (
            echo [UYARI] Python'da 'pywin32' paketi eksik ^(SAP GUI VA01 otomasyonu icin gerekli^).
            echo         Kurmak icin: "!PYTHON_EXE!" -m pip install pywin32
        ) else (
            echo [OK]    Python pywin32 ^(SAP GUI COM^) modulu hazir.
        )
    ) else (
        echo [UYARI] .env icindeki PYTHON_EXE bulunamadi: !PYTHON_EXE!
        echo         SAP VA01 direkt acma yerine .vbs indirme modu kullanilabilir.
    )
) else (
    echo [UYARI] PYTHON_EXE tanimli degil ^(.env^).
)

:: 5) SAP GUI Kontrolu
if defined SAP_LOGON_PATH (
    if exist "!SAP_LOGON_PATH!" (
        echo [OK]    SAP GUI bulundu: !SAP_LOGON_PATH!
    ) else (
        echo [UYARI] SAP GUI bu bilgisayarda bulunamadi: !SAP_LOGON_PATH!
    )
)

:: 6) PostgreSQL Erisim Kontrolu
powershell -NoProfile -Command "$t = New-Object Net.Sockets.TcpClient; try { $t.Connect('!DB_HOST!', !DB_PORT!); $t.Close(); exit 0 } catch { exit 1 }"
if errorlevel 1 (
    echo [HATA]  PostgreSQL veritabanina ulasilamiyor ^(!DB_HOST!:!DB_PORT!^)!
    echo         Lutfen PostgreSQL servisinin calistigindan ve .env ayarlarindan emin olun.
    pause
    exit /b 1
) else (
    echo [OK]    PostgreSQL baglantisi basarili ^(!DB_HOST!:!DB_PORT!^).
)

:: 7) Excel Dosyalari Kontrolu
set "XLSX_COUNT=0"
for %%f in ("*.xlsx") do set /a XLSX_COUNT+=1
echo [BILGI] SD klasorunde !XLSX_COUNT! adet .xlsx raporu bulundu.

:: 8) Frontend Paket Kontrolu
if not exist "frontend\node_modules" (
    echo.
    echo [ISLEM] Frontend bagimliliklari yukleniyor ^(npm install^)...
    pushd frontend
    call npm install --no-audit --no-fund
    if errorlevel 1 (
        echo [HATA]  npm install basarisiz oldu!
        popd
        pause
        exit /b 1
    )
    popd
)
echo [OK]    Frontend node_modules hazir.

:: 9) Backend Derleme Kontrolu
echo [ISLEM] Backend derleniyor ^(dotnet build^)...
pushd backend
dotnet build --nologo -v q
if errorlevel 1 (
    echo [HATA]  Backend derlemesi basarisiz oldu!
    popd
    pause
    exit /b 1
)
popd
echo [OK]    Backend basariyla derlendi.

echo.
echo ============================================================================
echo   SUNUCULAR BASLATILIYOR...
echo   - Backend API : http://localhost:!BACKEND_PORT!  ^(Swagger: /swagger^)
echo   - Frontend UI : http://localhost:!FRONTEND_PORT!
echo ============================================================================
echo.

start "UGES SD Backend (!BACKEND_PORT!)" cmd /k "cd /d "%~dp0backend" && dotnet run --no-build"
start "UGES SD Frontend (!FRONTEND_PORT!)" cmd /k "cd /d "%~dp0frontend" && npx vite --port !FRONTEND_PORT!"

timeout /t 3 /nobreak >nul
start "" "http://localhost:!FRONTEND_PORT!"

echo [TAMAM] Uygulama yeni pencerelerde baslatildi ve tarayicida acildi.
pause
