# HWEncParamViewer

[QSVEnc](https://github.com/rigaya/QSVEnc)/[NVEnc](https://github.com/rigaya/NVEnc)/[VCEEnc](https://github.com/rigaya/VCEEnc)のVPPフィルタの効果のプレビューを行います。

## 想定動作環境
- Windows 10/11 (x64)  
- Linux

## 使い方

アプリを開いたら、確認したい入力ファイルをドラッグドロップで開きます。

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


## ソースコードについて
- MITライセンスです。