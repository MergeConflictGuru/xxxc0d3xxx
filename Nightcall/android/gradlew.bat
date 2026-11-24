@echo off
setlocal
set "RELAY_JAVA=java.exe"
if defined JAVA_HOME set "RELAY_JAVA=%JAVA_HOME%\bin\java.exe"
"%RELAY_JAVA%" -classpath "%~dp0gradle\wrapper\gradle-wrapper.jar" org.gradle.wrapper.GradleWrapperMain %*
exit /b %ERRORLEVEL%
