# HWEncParamViewer

[QSVEnc](https://github.com/rigaya/QSVEnc)/[NVEnc](https://github.com/rigaya/NVEnc)/[VCEEnc](https://github.com/rigaya/VCEEnc)のVPPフィルタの効果のプレビューを行います。

## 想定動作環境
- Windows 10/11 (x64)  
- Linux

## 使い方

アプリを開いたら、確認したい入力ファイルをドラッグドロップで開きます。

エンコーダが未指定なら、本体と同じフォルダを探します。候補の順はWindowsでQSVEncC64.exe、NVEncC64.exe、VCEEncC64.exe、Linuxでqsvencc、nvencc、vceencc、rkmppencです。同じフォルダに見つからない場合、LinuxだけはPATHの絶対パス要素を同じ候補順で探します。空要素と相対フォルダは探しません。Linuxでは実行権限のあるファイルだけを選びます。Windowsの32bit版は候補にしません。

自動で選んだ経路には「自動選択」と表示し、設定へは保存せず次回の起動で探し直します。利用者が指定した経路がある場合はそれを優先し、ファイルがなくても別のエンコーダへ切り替えません。見つかった候補がGPUに対応しない場合は、利用者がエンコーダを選び直してください。起動時の探索ではGPUの検査は行いません。

<img src="./data/HWEncParamViewer_20261107_01.jpg" width="480">

右側で有効にしたいフィルタを追加して、パラメータ調整して効き具合を確認してください。

<img src="./data/HWEncParamViewer_20261107_02.jpg" width="600">


## 対応エンコーダ

- [QSVEnc](https://github.com/rigaya/QSVEnc)
- [NVEnc](https://github.com/rigaya/NVEnc)
- [VCEEnc](https://github.com/rigaya/VCEEnc)

## ビルド方法

### Windows

MSVC2026をインストールし、`HWEncParamViewer.sln`を開いてビルドしてください。

### Linux

[.NET 10 SDKをインストール](https://dotnet.microsoft.com/ja-jp/download/dotnet/10.0)し、リポジトリのルートで次のコマンドを実行してください。初回ビルド時にNuGetパッケージが復元されます。

```bash
dotnet build src/HWEncParamViewer.App/HWEncParamViewer.App.csproj -c Release
dotnet run --project src/HWEncParamViewer.App/HWEncParamViewer.App.csproj -c Release --no-build
```


## 版とライセンス

版はウィンドウのタイトルと`--version`で確認できます。開発時の定義は`Directory.Build.props`にあり、`scripts/get-version.sh`でも同じ文字列を取得できます。

ソースコードは[MITライセンス](LICENSE)です。依存ライブラリの著作権とライセンス本文は、リポジトリの[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)に記載しています。