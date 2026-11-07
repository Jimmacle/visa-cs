#!/bin/sh
# Builds the NI-488.2 stand-ins used by GpibNativeDriverTests (Linux/macOS):
#   libfake_ni4882.so           NI-488.2 like: asynchronous calls and ibnotify callbacks
#   libfake_ni4882_nonotify.so  linux-gpib like: asynchronous calls, no ibnotify (completion is polled)
#   libfake_ni4882_noasync.so   an older library without asynchronous calls
set -e
cd "$(dirname "$0")"
cc -shared -fPIC -O2 -Wall -pthread -o libfake_ni4882.so fake_ni4882.c
cc -shared -fPIC -O2 -Wall -pthread -DFAKE_NO_NOTIFY -o libfake_ni4882_nonotify.so fake_ni4882.c
cc -shared -fPIC -O2 -Wall -pthread -DFAKE_NO_ASYNC -o libfake_ni4882_noasync.so fake_ni4882.c
echo "Built $(pwd)/libfake_ni4882.so (and the _nonotify and _noasync variants)"
echo "Run tests with VISACS_TEST_GPIB_LIBRARY=$(pwd)/libfake_ni4882.so (or one of the variants)"
