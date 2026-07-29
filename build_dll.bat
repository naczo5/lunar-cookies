@echo off
setlocal EnableExtensions
pushd "%~dp0"

set "JAVA_HOME=%JAVA_HOME%"
if not defined JAVA_HOME set "JAVA_HOME=C:\Program Files\Eclipse Adoptium\jdk-8.0.492.9-hotspot"
set "JAVAC=%JAVA_HOME%\bin\javac.exe"
set "GPP="
if exist "C:\mingw64\mingw64\bin\g++.exe" set "GPP=C:\mingw64\mingw64\bin\g++.exe"
if not defined GPP if exist "C:\msys64\mingw64\bin\g++.exe" set "GPP=C:\msys64\mingw64\bin\g++.exe"
if not defined GPP (
  where g++ >nul 2>&1 && for /f "delims=" %%i in ('where g++') do (
    set "GPP=%%i"
    goto :gpp_found
  )
)
:gpp_found
if not defined GPP (
  echo g++ not found. Install MinGW-w64 x86_64.
  popd
  exit /b 1
)
echo Using GPP=%GPP%
"%GPP%" -dumpmachine


echo [1/3] Compiling SessionSwitcher.java ...
if not exist "JavaHelper\out" mkdir "JavaHelper\out"
"%JAVAC%" -source 1.8 -target 1.8 -d "JavaHelper\out" "JavaHelper\SessionSwitcher.java"
if errorlevel 1 (
  echo javac failed
  popd
  exit /b 1
)

echo [2/3] Embedding class bytes ...
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\Embed-ClassBytes.ps1"
if errorlevel 1 (
  echo Embed failed
  popd
  exit /b 1
)

echo [3/3] Building switcher.dll ...
"%GPP%" -m64 -std=c++17 -shared -O2 -fno-exceptions -fno-rtti -o "Bridge\switcher.dll" "Bridge\bridge.cpp" ^
  -I"%JAVA_HOME%\include" -I"%JAVA_HOME%\include\win32" -I"Bridge" ^
  -lws2_32 -static-libgcc -static-libstdc++ -Wl,--kill-at
if errorlevel 1 (
  echo g++ failed
  popd
  exit /b 1
)

echo Copied targets:
copy /Y "Bridge\switcher.dll" "LunarCookies\switcher.dll" >nul
echo   LunarCookies\switcher.dll
if exist "LunarCookies\bin\Debug\net8.0-windows\" (
  copy /Y "Bridge\switcher.dll" "LunarCookies\bin\Debug\net8.0-windows\switcher.dll" >nul
  echo   Debug output
)
if exist "LunarCookies\bin\Release\net8.0-windows\" (
  copy /Y "Bridge\switcher.dll" "LunarCookies\bin\Release\net8.0-windows\switcher.dll" >nul
  echo   Release output
)

echo Done: Bridge\switcher.dll
popd
endlocal
