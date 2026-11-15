using System.Runtime.CompilerServices;

// 時計guardの境界条件をプロセス起動なしで検証できるようにする。
[assembly: InternalsVisibleTo("HWEncVppPreview.Tests")]

[assembly: InternalsVisibleTo("HWEncVppPreview")]

[assembly: InternalsVisibleTo("HWEncVppPreview.App.Tests")]

// 撮影用の入力検査を実プロセスなしで通す。
[assembly: InternalsVisibleTo("UiSnapshot")]
