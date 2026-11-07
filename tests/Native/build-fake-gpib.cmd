@echo off
rem Builds the NI-488.2 stand-ins used by GpibNativeDriverTests (Windows, from a VS developer prompt or via vcvars64.bat):
rem   fake_ni4882.dll           NI-488.2 like: asynchronous calls and ibnotify callbacks
rem   fake_ni4882_nonotify.dll  linux-gpib like: asynchronous calls, no ibnotify (completion is polled)
rem   fake_ni4882_noasync.dll   an older library without asynchronous calls
pushd "%~dp0"
cl /nologo /LD /O2 /W3 fake_ni4882.c /Fo:fake_ni4882.obj /Fe:fake_ni4882.dll || goto failed
cl /nologo /LD /O2 /W3 /DFAKE_NO_NOTIFY fake_ni4882.c /Fo:fake_ni4882_nonotify.obj /Fe:fake_ni4882_nonotify.dll || goto failed
cl /nologo /LD /O2 /W3 /DFAKE_NO_ASYNC fake_ni4882.c /Fo:fake_ni4882_noasync.obj /Fe:fake_ni4882_noasync.dll || goto failed
echo Built %CD%\fake_ni4882.dll (and the _nonotify and _noasync variants)
echo Run tests with VISACS_TEST_GPIB_LIBRARY=%CD%\fake_ni4882.dll (or one of the variants)
popd
exit /b 0
:failed
popd
exit /b 1
