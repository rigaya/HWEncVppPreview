# 偽エンコーダ

実GPUを使わず、都度起動と常駐プロトコルのプロセス制御を検証する.NETコンソールアプリ。
`dotnet build`で生成されるapphostを直接起動する。

通常の`--frames N`、`--seek h:mm:ss.fff`、VPP引数を受け取る。
出力の既定は64×36のy4mで、画素はVPP引数のSHA-256とフレーム番号から決まる。
`Xts`はseek位置からの相対秒数、`Xdur`は通常1/30秒。間引いた場合はstep/30秒になる。
`XPID`はテスト時のプロセス回収確認用、`XSEEK`は受け取ったseek値の確認用の拡張タグ。

`--preview-server`で起動すると、READYと要求ごとのBEGIN/y4m/ENDを出力する。
run/cancel/quitとstdinのEOFに対応し、新しいrunは実行中の要求を中断する。

|専用引数|動作|
|---|---|
|`--fake-hang-after N`|フレーム番号Nの直前で無応答になる。cancelには応答する|
|`--fake-ignore-cancel`|中断を無視する。上の無応答と組み合わせてkillを検証する|
|`--fake-crash-at N`|フレーム番号Nのデータを半分だけ書き、終了コード72で落ちる|
|`--fake-bad-control-at N`|フレーム番号Nの直前に不正な制御行を出し、無応答になる|
|`--fake-rc N`|フレーム出力後に指定コードで終了する。常駐では負数もENDに保持する|
|`--fake-step N`|フレーム番号をNずつ進め、間引いたXtsを出す|
|`--fake-delay-ms N`|各フレームを出す前に指定ミリ秒待つ。テストの時間判定には使わない|
|`--fake-gate PATH --fake-gate-after N`|フレーム番号N以降はPATHが作られるまで待つ。FileSystemWatcherで進行を同期する|
|`--fake-duration VALUE`|入力ログと同じDuration行をフレームより前にstderrへ書く。N/Aや不正値も指定可能|
|`--fake-stderr-bytes N`|要求開始時にstderrへN文字書き、パイプ詰まりを検証する|

|環境変数|動作|
|---|---|
|`FAKE_ENCODER_READY_NONE=1`|常駐起動時のREADYを省略する|
|`FAKE_ENCODER_DROP_AFTER=N`|常駐でN件のENDを書いた後、終了コード71で落ちる|
|`FAKE_ENCODER_PID_FILE=PATH`|起動するたびにプロセスIDをPATHへ改行区切りで追記する|

BackendTestsは実時間のsleepを使わず、出力イベント・パイプの完了・手動時計で同期する。
実時間のWaitAsyncはテスト自体の無限停止を防ぐための上限だけに使う。

`FAKE_ENCODER_LOG_READY_FILE`を指定すると、stderrへFAKEPIDを書き終えた直後に空ファイルを作る。タイムアウト検証でログ出力と偽時計の進行を同期するために使う。

`--version`は`FakeEncoder 1.0`、`--help`は簡易ヘルプをstdoutへ出して即座に終了する。ViewModelのメタデータ取得とヘルプ表示の検証に使う。

V5の比較用に`--fake-sar 4:3`でSARを指定できる。`--output-res WxH`は実際のヘッダとフレームデータの大きさを変更する。`--vpp-decimate`を指定した要求は既定で2枚ごとの出力にし、VPP引数が空の要求とのフレーム数・時刻選択の差を再現する。`--fake-step`を明示した場合はそちらを優先する。
