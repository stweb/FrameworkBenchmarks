#!/bin/bash

fw_depends monox

#extra cleaning
rm -rf suave/bin suave/obj

xbuild suave/suave.fsproj /t:Clean
xbuild suave/suave.fsproj

# export MONO_GC_PARAMS=nursery-size=64m

mono -O=all $TROOT/suave/bin/Release/suave.exe $MAX_THREADS &
