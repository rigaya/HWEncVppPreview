# 画面移動の計測

実エンコーダの移動操作と描画の時系列を採取するための専用プロジェクト。通常のソリューションとCIには含めない。

Windowsで`HWENC_VIEWER_BENCH_ENCODER`と`HWENC_VIEWER_BENCH_INPUT`に実行ファイルと入力動画を指定し、`HWENC_VIEWER_BENCH_TRACE`に性能記録先のファイルを指定して、次を実行する。

```text
dotnet test tools/NavigationBenchmark/NavigationBenchmark.csproj -c Release
```

`HWENC_VIEWER_BENCH_SCENARIO`は`S1`・`S2`・`S3`、`HWENC_VIEWER_BENCH_FILTER`は`F0`・`F1`・`F2`を選べる。未指定時は`S1`と`F0`。入力操作後の待機を計測するときは`HWENC_VIEWER_BENCH_IDLE_HOLD_MS`を指定する。
