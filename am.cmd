@echo off
rem Builds once, then runs the compiled binary directly (not 'dotnet run')
rem for the same reason as am.sh: avoids the build/launch wrapper process
rem sitting between Ctrl+C and the app.
dotnet build "%~dp0AutomowerConsole\AutomowerConsole.csproj" -v quiet
if errorlevel 1 exit /b 1
dotnet "%~dp0AutomowerConsole\bin\Debug\net10.0\AutomowerConsole.dll" %*
