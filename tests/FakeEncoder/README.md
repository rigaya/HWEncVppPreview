# 偽エンコーダ

実GPUを使わず、常駐プロトコルと入力長・help取得のプロセス制御を検証する.NETコンソールアプリ。
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
|`--fake-gate-pipe NAME --fake-gate-after N`|フレーム番号Nの直前で指定パイプに接続し、解除指示を待つ|
|`--fake-frame-pipe NAME`|最初の画素を出す前に指定パイプで解除指示を待つ|
|`--fake-body-pipe NAME`|指定フレームの画素を半分出した後に指定パイプで解除指示を待つ|
|`--fake-duration VALUE`|入力ログと同じDuration行をフレームより前にstderrへ書く。N/Aや不正値も指定可能|
|`--fake-stderr-bytes N`|要求開始時にstderrへN文字書き、パイプ詰まりを検証する|

|環境変数|動作|
|---|---|
|`FAKE_ENCODER_READY_NONE=1`|常駐起動時のREADYを省略する|
|`FAKE_ENCODER_READY_VERSION=0`|常駐起動時に未対応の版を通知する|
|`FAKE_ENCODER_PID_PIPE=NAME`|起動時に指定パイプへプロセス番号を送る|
|`FAKE_ENCODER_INITIALIZE_PIPE=NAME`|初期化の開始を指定パイプへ通知し、解除指示を待つ|
|`FAKE_ENCODER_PROBE_PIPE=NAME`|履歴検査の開始を指定パイプへ通知し、解除指示を待つ|
|`FAKE_ENCODER_HOLD_OUTPUT_PIPE=NAME`|標準出力と標準エラーを継承する子を起動する。子は指定パイプにPIDを通知し、`release`を受け取ったら終了コード`0`を返して終了する。制御パイプのEOFでも終了する。`check`を先に送ると、子は継承した標準出力へ`held`を一行書き、解除指示かEOFを待つ|
|`FAKE_ENCODER_DROP_AFTER=N`|常駐でN件のENDを書いた後、終了コード71で落ちる|
|`FAKE_ENCODER_PID_FILE=PATH`|起動するたびにプロセスIDをPATHへ改行区切りで追記する|

BackendTestsは実時間のsleepを使わず、出力イベント・パイプの完了・手動時計で同期する。
実時間のWaitAsyncはテスト自体の無限停止を防ぐための上限だけに使う。

同期パイプは要求側が用意し、偽エンコーダが接続後に到達を一行で通知する。要求側の`release`で再開し、取消時は待機を打ち切る。

`--version`は`FakeEncoder 1.0`、`--help`は簡易ヘルプをstdoutへ出して即座に終了する。ViewModelのメタデータ取得とヘルプ表示の検証に使う。

V5の比較用に`--fake-sar 4:3`でSARを指定できる。`--output-res WxH`は実際のヘッダとフレームデータの大きさを変更する。`--vpp-decimate`を指定した要求は既定で2枚ごとの出力にし、VPP引数が空の要求とのフレーム数・時刻選択の差を再現する。`--fake-step`を明示した場合はそちらを優先する。

`--fake-duration-at-exit`は`--fake-duration`の行を終了直前まで遅らせる。`--fake-duration-exit-pipe NAME`を併用すると、その直前で指定パイプの解除指示を待つ。

V5.2のレート変更用に`--fake-output-rate double|film|vfr`を指定できる。入力は30fpsとして`--frames`を数え、doubleは60fps、filmは24fps、vfrはヘッダ60fpsで間隔2・3・1・4tick（1tick=1/60秒）を繰り返す。変更時の画素番号と時刻列はseek前後で同じ入力位置を表す。`--fake-no-xdur`は全フレームのXdurを省略する。VPP引数側に指定すると入力検査は30fpsのままで、フィルタ後だけ出力レートを変える条件を再現できる。
