#!/bin/bash
# Mono mcs로 .NET Framework 4.x용 실행 파일을 만든다 (Windows 10/11 기본 포함 .NET Framework 4.8에서 실행)
set -e
cd "$(dirname "$0")"
mkdir -p out
RES=""
for f in fonts/*.ttf; do RES="$RES -resource:$f,$(basename $f)"; done
mcs -sdk:4.5 -target:winexe -optimize+ -platform:anycpu -langversion:7 -codepage:utf8 \
  -win32icon:app.ico -out:out/UsageQuest.exe \
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Web.Extensions.dll -r:System.Management.dll \
  $RES src/*.cs
ls -la out/UsageQuest.exe
