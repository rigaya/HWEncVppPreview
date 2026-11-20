#!/usr/bin/env python3
"""試験に再導入された時間待ち、非公開反射、ファイル合図を検出する。"""
from pathlib import Path
import re
import sys

root = Path(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1] != '--self-test' else Path(__file__).resolve().parents[1]
checks = (
    ('時間待ち', r'\b(?:Task\s*\.\s*(?:Delay|Yield)|Thread\s*\.\s*(?:Sleep|Yield)|SpinWait\s*\.\s*[A-Za-z_]\w*)\s*\('),
    ('ポーリング補助', r'\bWaitUntilAsync\s*\('),
    ('時間付き待機', r'\.\s*WaitOne\s*\(\s*(?:\d|TimeSpan\s*\.)'),
    ('画面処理の反復', r'\b(?:for|while)\s*\([^\n]*\)[^\n]*(?:RunJobs\s*\(|InvokeAsync\s*\()'),
    ('非公開反射', r'\bBindingFlags\s*\.\s*NonPublic\b|\.\s*(?:GetField|GetNestedType|GetMethod|GetConstructor|GetConstructors)\s*\('),
    ('非公開型の反射', r'\.\s*GetType\s*\(\s*\)\s*\.\s*GetProperty\s*\(|Activator\s*\.\s*CreateInstance\s*\('),
    ('ファイル監視', r'\bFileSystemWatcher\b'),
    ('ファイル待ち', r'\b(?:while|for)\s*\([^\n]*(?:File\s*\.\s*Exists|FakeLogFiles\s*\.\s*Exists)\s*\('),
    ('旧共有ファイル合図', r'FAKE_ENCODER_[A-Z_]*(?:READY|RELEASE|GATE|EXIT)_(?:FILE|PATH)\b|--fake-[a-z-]*(?:ready|release|gate)-(?:file|path)\b'),
)
compiled = [(name, re.compile(pattern)) for name, pattern in checks]
process_type = r'(?:ServerBackend|OneShotBackend|InputProbe)'
qualified_process_type = r'(?:(?:global|\w+)\s*::\s*)?(?:\w+\s*\.\s*)*' + process_type
direct_process = re.compile(
    r'\bnew\s+' + qualified_process_type + r'\s*[({]'
    r'|\b' + process_type + r'\??\s+\w+\s*(?:=|=>|\{[^{}]*\}\s*=|\([^()]*\)\s*=>)\s*new\s*\('
)
# 文字列とコメント内の記述を検査対象から外し、改行を保って行番号を維持する。
non_code = re.compile(r"""//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'""")


def code_only(source):
    return non_code.sub(lambda match: re.sub(r'[^\n]', ' ', match.group()), source)


if len(sys.argv) > 1 and sys.argv[1] == '--self-test':
    examples = {
        '時間待ち': ['await Task.Delay(10);', 'SpinWait.SpinUntil(check);'],
        'ポーリング補助': ['await WaitUntilAsync(check);'],
        '時間付き待機': ['handle.WaitOne(5);'],
        '画面処理の反復': ['for (int i=0; i<3; i++) Dispatcher.UIThread.RunJobs();', 'for (int i=0; i<3; i++) await Dispatcher.UIThread.InvokeAsync(action);'],
        '非公開反射': ['BindingFlags.NonPublic', 'type.GetField(name);'],
        '非公開型の反射': ['value.GetType().GetProperty(name);'],
        'ファイル監視': ['new FileSystemWatcher(path);'],
        'ファイル待ち': ['while (!File.Exists(path))'],
        '旧共有ファイル合図': ['FAKE_ENCODER_READY_FILE'],
    }
    for name, pattern in compiled:
        if any(not pattern.search(example) for example in examples[name]):
            raise SystemExit(name + 'を検出できません')
    for safe in ('root.GetProperty("rc")', 'Assert.True(File.Exists(resultPath));', 'FAKE_ENCODER_CHILD_READY_PIPE'):
        if any(pattern.search(safe) for _, pattern in compiled):
            raise SystemExit('正常な検査を誤検出しました: ' + safe)
    if not re.search(r'\.GetProperty\s*\([^\n]*\)\s*!?\s*\.SetValue\s*\(',
                     'type.GetProperty(nameof(Value))!\n    .SetValue(target, value);'):
        raise SystemExit('行をまたぐsetterへの反射を検出できません')
    constructors = [
        'new ServerBackend()',
        'new global::HWEncVppPreview.Core.\n InputProbe()',
        'new\n  HWEncVppPreview.Core.OneShotBackend()',
        'private readonly ServerBackend _server =\n new();',
        'new ServerBackend { };',
        'internal InputProbe Probe { get; } = new();',
        'internal static InputProbe Create()\n => new(clock);',
        'private static HWEncVppPreview.Core.ServerBackend Backend(Action<int>? configure = null) => new();',
    ]
    if any(not direct_process.search(code_only(example)) for example in constructors):
        raise SystemExit('試験でのプロセス直接構築を検出できません')
    indirect = [
        'TestProcesses.CreateServerBackend()',
        'new InputProbeException("失敗")',
        '// new ServerBackend()',
        'const string example = "new InputProbe()";',
    ]
    if any(direct_process.search(code_only(example)) for example in indirect):
        raise SystemExit('試験でのプロセス間接構築を誤検出しました')
    print('試験検査の自己確認に成功しました')
    sys.exit(0)
issues = []
for path in sorted((root / 'tests').rglob('*.cs')):
    if any(part in ('bin', 'obj') for part in path.parts):
        continue
    for number, line in enumerate(path.read_text(encoding='utf-8-sig').splitlines(), 1):
        content = line.split('//', 1)[0]
        for name, pattern in compiled:
            if pattern.search(content):
                issues.append((path.relative_to(root), number, name, line.strip()))
    # 直接記述された混在を捕捉する。補助経由の依存関係は別途、呼出経路を確認する。
    source = path.read_text(encoding='utf-8-sig')
    relative = path.relative_to(root)
    if relative != Path('tests/TestProcesses.cs'):
        for match in direct_process.finditer(code_only(source)):
            number = source.count('\n', 0, match.start()) + 1
            issues.append((relative, number, 'プロセスの直接構築', 'TestProcessesの作成補助を使ってください'))
    # setterへの反射は行をまたぐことがあるため、行単位の検査とは別に拾う。
    for match in re.finditer(r'\.GetProperty\s*\([^\n]*\)\s*!?\s*\.SetValue\s*\(', source):
        number = source.count('\n', 0, match.start()) + 1
        issues.append((path.relative_to(root), number, 'setterへの反射', match.group().replace('\n', ' ')))
    method = re.compile(r'\b(?:public|private|internal)\s+(?:static\s+)?(?:async\s+)?(?:Task(?:<[^>]+>)?|void)\s+(\w+)\s*\([^)]*\)\s*\{')
    for match in method.finditer(source):
        depth, end = 1, match.end()
        while end < len(source) and depth:
            if source[end] == '{': depth += 1
            elif source[end] == '}': depth -= 1
            end += 1
        body = source[match.end():end]
        if re.search(r'\bnew\s+FakeTimeProvider\s*\(', body) and re.search(
                r'\b(?:new\s+(?:ServerBackend|OneShotBackend)|TestProcesses\s*\.\s*CreateServerBackend\w*|Process\s*\.\s*Start)\s*\(', body):
            number = source.count('\n', 0, match.start()) + 1
            issues.append((path.relative_to(root), number, '偽時計と実プロセスの直接混在', match.group(1)))
for path, number, name, line in issues:
    print(f'{path}:{number}: {name}: {line}')
print(f'違反候補: {len(issues)}件')
sys.exit(bool(issues))
