@echo off
setlocal
pushd "%~dp0"

if not exist "Bridge\switcher.dll" (
  echo Building switcher.dll first...
  call build_dll.bat
  if errorlevel 1 exit /b 1
)

echo Publishing unpackaged self-contained WinUI 3 host...
dotnet publish "LunarCookies\LunarCookies.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -o "publish"
if errorlevel 1 exit /b 1

if exist "publish\LunarCookies.pdb" del /Q "publish\LunarCookies.pdb"
copy /Y "Bridge\switcher.dll" "publish\switcher.dll" >nul
echo Published to publish\
popd
endlocal
