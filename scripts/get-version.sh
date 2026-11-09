#!/bin/sh

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPOSITORY_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
VERSION_PROPS="$REPOSITORY_ROOT/Directory.Build.props"

# 定義の欠落や重複を見逃すと、版と配布名が一致しなくなるため一つだけに限定する。
VERSION_LINES=$(sed -n 's/^[[:space:]]*<HWEncParamViewerVersion>\([^<]*\)<\/HWEncParamViewerVersion>[[:space:]]*$/v=\1/p' "$VERSION_PROPS" | tr -d '\r')
VERSION_COUNT=$(printf '%s\n' "$VERSION_LINES" | awk 'NF { count++ } END { print count + 0 }')
if [ "$VERSION_COUNT" -ne 1 ]; then
    echo "ERROR: HWEncParamViewerVersionは一つだけ定義してください。" >&2
    exit 1
fi

VERSION=${VERSION_LINES#v=}
if ! printf '%s\n' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]{2}$'; then
    echo "ERROR: HWEncParamViewerVersionの形式が不正です: '$VERSION'" >&2
    exit 1
fi

printf '%s\n' "$VERSION"
