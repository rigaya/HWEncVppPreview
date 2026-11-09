#!/usr/bin/env bash
set -euo pipefail

# 既存のタグを上書きせず、同じ実行が作った下書きだけを再開する。
mode="${1:-}"
if [[ "$mode" != check && "$mode" != create ]]; then
    echo 'ERROR: checkまたはcreateを指定してください。' >&2
    exit 1
fi
: "${GH_TOKEN:?RELEASE_PATが必要です}"
: "${GITHUB_REPOSITORY:?対象リポジトリが必要です}"
: "${GITHUB_RUN_ID:?実行番号が必要です}"
: "${GITHUB_RUN_ATTEMPT:?再実行回数が必要です}"
: "${TAG:?版が必要です}"
: "${RELEASE_SHA:?対象コミットが必要です}"
if [[ ! "$TAG" =~ ^[0-9]+\.[0-9]{2}$ || ! "$RELEASE_SHA" =~ ^[0-9a-f]{40}$ ||
      ! "$GITHUB_RUN_ID" =~ ^[0-9]+$ || ! "$GITHUB_RUN_ATTEMPT" =~ ^[1-9][0-9]*$ ]]; then
    echo 'ERROR: 版・コミット・実行番号の形式が不正です。' >&2
    exit 1
fi

# 作成を始める前に、数・名前・空でないことをまとめて検査する。
if [[ "$mode" == create ]]; then
    : "${ASSET_DIR:?成果物の場所が必要です}"
    mapfile -d '' assets < <(find "$ASSET_DIR" -type f -print0)
    windows_count=0
    linux_count=0
    for asset in "${assets[@]}"; do
        if [[ ! -s "$asset" ]]; then
            echo 'ERROR: 空の成果物があります。' >&2
            exit 1
        fi
        case "${asset##*/}" in
            "HWEncParamViewer_${TAG}_win-x64.zip") windows_count=$((windows_count + 1)) ;;
            "HWEncParamViewer_${TAG}_linux-x64.tar.xz") linux_count=$((linux_count + 1)) ;;
            *) echo 'ERROR: 想定外の名前の成果物があります。' >&2; exit 1 ;;
        esac
    done
    if [[ "${#assets[@]}" -ne 2 || "$windows_count" -ne 1 || "$linux_count" -ne 1 ]]; then
        echo 'ERROR: 指定版のWindows zipとLinux tar.xzが非空で1個ずつ必要です。' >&2
        exit 1
    fi
fi

run_marker="github-actions-run:$GITHUB_RUN_ID"
api_error="$(mktemp)"
trap 'rm -f "$api_error"' EXIT

# 404だけを未作成と扱い、権限不足や通信失敗は作成処理へ進ませない。
get_optional() {
    if api_json="$(gh api "$1" 2>"$api_error")"; then
        return 0
    elif grep -q '(HTTP 404)' "$api_error"; then
        return 1
    else
        echo 'ERROR: GitHub上の既存データの確認に失敗しました。' >&2
        cat "$api_error" >&2
        return 2
    fi
}

tag_exists=false
if get_optional "/repos/$GITHUB_REPOSITORY/git/ref/tags/$TAG"; then
    if [[ "$GITHUB_RUN_ATTEMPT" -le 1 || "$(jq -r '.object.type' <<< "$api_json")" != tag ]]; then
        echo 'ERROR: 既存のタグはこの実行の再開対象ではありません。版を更新してください。' >&2
        exit 1
    fi
    tag_object="$(jq -r '.object.sha' <<< "$api_json")"
    tag_json="$(gh api "/repos/$GITHUB_REPOSITORY/git/tags/$tag_object")"
    if [[ "$(jq -r '.object.type' <<< "$tag_json")" != commit ||
          "$(jq -r '.object.sha' <<< "$tag_json")" != "$RELEASE_SHA" ||
          "$(jq -r '.message' <<< "$tag_json")" != "$run_marker" ]]; then
        echo 'ERROR: 既存タグの対象コミットまたは実行の印が一致しません。' >&2
        exit 1
    fi
    tag_exists=true
else
    status=$?
    [[ "$status" -eq 1 ]] || exit "$status"
fi

release_exists=false
if get_optional "/repos/$GITHUB_REPOSITORY/releases/tags/$TAG"; then
    body="$(jq -r '.body // ""' <<< "$api_json")"
    if [[ "$tag_exists" != true || "$(jq -r '.draft' <<< "$api_json")" != true ||
          "$(jq -r '.tag_name' <<< "$api_json")" != "$TAG" ||
          "$body" != *"<!-- $run_marker -->"* ]]; then
        echo 'ERROR: 既存Releaseは同じ実行が作成した下書きではありません。' >&2
        exit 1
    fi
    release_exists=true
else
    status=$?
    [[ "$status" -eq 1 ]] || exit "$status"
fi

if [[ "$mode" == check ]]; then
    echo 'タグとReleaseの事前条件を確認しました。'
    exit 0
fi

if [[ "$tag_exists" != true ]]; then
    tag_json="$(gh api --method POST "/repos/$GITHUB_REPOSITORY/git/tags" \
        -f tag="$TAG" -f message="$run_marker" -f object="$RELEASE_SHA" -f type=commit)"
    tag_object="$(jq -er '.sha' <<< "$tag_json")"
    gh api --method POST "/repos/$GITHUB_REPOSITORY/git/refs" \
        -f ref="refs/tags/$TAG" -f sha="$tag_object" >/dev/null
fi
if [[ "$release_exists" != true ]]; then
    gh release create "$TAG" --repo "$GITHUB_REPOSITORY" --target "$RELEASE_SHA" \
        --verify-tag --draft --title "$TAG" \
        --notes "<!-- $run_marker -->

成果物を確認し、説明を書いてから手動で公開してください。"
fi
gh release upload "$TAG" "${assets[@]}" --repo "$GITHUB_REPOSITORY" --clobber
release_url="$(gh release view "$TAG" --repo "$GITHUB_REPOSITORY" --json url --jq '.url')"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    echo "release_url=$release_url" >> "$GITHUB_OUTPUT"
fi
printf '下書き: %s\n' "$release_url"
