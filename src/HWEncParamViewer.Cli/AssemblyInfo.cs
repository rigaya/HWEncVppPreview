using System.Runtime.CompilerServices;

// 実プロセスの捕捉を、GPUや公開用のCore入口を増やさず単体検査する。
[assembly: InternalsVisibleTo("HWEncParamViewer.Tests")]
