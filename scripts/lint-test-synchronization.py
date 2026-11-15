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
                r'\bnew\s+(?:ServerBackend|OneShotBackend)\s*\(|\bProcess\s*\.\s*Start\s*\(', body):
            number = source.count('\n', 0, match.start()) + 1
            issues.append((path.relative_to(root), number, '偽時計と実プロセスの直接混在', match.group(1)))
for path, number, name, line in issues:
    print(f'{path}:{number}: {name}: {line}')
print(f'違反候補: {len(issues)}件')
sys.exit(bool(issues))
