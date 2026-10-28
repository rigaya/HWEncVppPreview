# ヘッドレス実行

```text
HWEncParamViewer.Cli --enc <エンコーダ> --input <入力> --time <秒数またはh:mm:ss.fff>
    [--vpp "<オプションテキスト>"] [--input-args "<入力引数>"]
    [--pre-margin <秒数>] [--post-margin <秒数>]
    [--mode auto|server|oneshot] [--watchdog-seconds <秒数>]
    [--dump <生画像ファイル>] [--dump-y4m <区間ファイル>]
```

入力引数の既定は`--avhw`、前マージンは2秒、後マージンは0.5秒、方式は`auto`。
`--vpp`と`--input-args`はCoreの`OptionTextParser`で解釈する。
エンコーダと入力のパスはそのまま渡すため、WSLからWindowsのエンコーダを使う場合の入力パスはWindows形式で指定する。

`auto`は常駐を試し、READY非対応なら都度起動へ切り替える。
`server`は常駐だけを使い、READY非対応をエラーとする。
`oneshot`は都度起動だけを使う。いずれも画像選択には`PreviewController`をdebounceなしで使用する。

`--watchdog-seconds`の既定は30秒。入力情報取得では検査全体の上限、プレビューではstdoutが進まない時間の上限となる。
GPU混雑時の経過時間は情報としてJSONへ記録し、性能の合否判定には使わない。
入力情報の検査は最大300フレームを要求し、最初の完全なフレームを受信した時点でプロセスを停止して入力形式を確定する。

stdoutには結果JSONを1行だけ出す。`rc`はエンコーダの終了コード、エンコーダ実行前などのエラーではCLIのエラーコード。
`mode`は実際に使用した方式、`selectedIndex`は区間先頭からの0始まりの選択位置。
`seekSeconds`がnullのときは先頭から実行する。`frames`は要求枚数、`frameCount`は実際に受け取った枚数。
`sha256`は選択した生画像の小文字16進ハッシュ。`timedOut`はタイムアウト判定を示す。
エラー時は`error`と`stderrTail`（標準エラーの末尾、最大8192文字）を含む。

`--dump`は選んだ1フレームの生画像、`--dump-y4m`は区間全体の画像と時刻を出力する。
既存ファイルは上書きする。

終了コードは成功0、エンコーダのエラー1、引数の誤り2、タイムアウトやその他の障害3。

テストなどのホストからは`Program.RunAsync(string[], TextWriter, TimeProvider?, CancellationToken)`を使い、
時間依存処理へ偽時計を注入できる。GUI向けAPIはCoreの`PreviewController`と`InputProbe`を使う。
