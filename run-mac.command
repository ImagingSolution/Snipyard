#!/bin/zsh
# Double-click in Finder to build (if needed) and run Snipyard on macOS.
# The .NET SDK lives in ~/.dotnet (installed via dotnet-install.sh).

cd "$(dirname "$0")" || exit 1

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet が見つかりません ($DOTNET_ROOT)。.NET SDK をインストールしてください。"
    read -k 1 "?何かキーを押すと閉じます..."
    exit 1
fi

dotnet run
status=$?

if [ $status -ne 0 ]; then
    echo
    echo "終了コード $status で終了しました。"
    read -k 1 "?何かキーを押すと閉じます..."
fi
exit $status
