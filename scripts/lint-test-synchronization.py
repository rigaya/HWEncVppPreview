#!/usr/bin/env python3
"""試験に再導入された時間待ち、非公開反射、ファイル合図を検出する。"""
from pathlib import Path
import re
import sys

root = Path(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1] != '--self-test' else Path(__file__).resolve().parents[1]
checks = (
    ('時間待ち', r'\b(?:Task\s*\.\s*(?:Delay|Yield)|Thread\s*\.\s*(?:Sleep|Yield)|SpinWait\s*\.\s*[A-Za-z_]\w*)\s*\('),
    ('ポーリング補助', r'\bWaitUntilAsync\s*\('),
    ('時間付き待機', r'\.\s*WaitOne\s*\(\s*[^)\s]'),
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


core_tests = Path('tests/HWEncVppPreview.Tests')
fixture = Path('tests/TestProcesses.cs')
validation = core_tests / 'PreviewTimeoutsTests.cs'
server_deadlines = core_tests / 'ServerDeadlineTests.cs'
cli_capture = core_tests / 'CliHelpCheckTests.cs'
vm_shutdown = Path('tests/HWEncVppPreview.App.Tests/StreamingNavigationTests.cs')
fake_clock = re.compile(r'\bnew\s+(?:[\w.]+\.)?FakeTimeProvider\s*\(')
real_process = re.compile(
    r'\b(?:TestProcesses\s*\.\s*CreateServerBackend|Process(?:Support)?\s*\.\s*Start|'
    r'CliProgram\s*\.\s*RunAsync|CaptureUntilExitAsync)\s*\('
    r'|\bTestProcesses\s*\.\s*CreateInputProbe\s*\(\s*(?:configureStartInfo\s*:|\))'
)
finite_product = re.compile(
    r'\bPreviewTimeouts\s*\.\s*(?:Default\b|Infinite\s+with\b)'
    r'|\bnew\s+(?:(?:global|\w+)\s*::\s*)?(?:\w+\s*\.\s*)*PreviewTimeouts\s*[({]'
    r'|\b(?:CreateServerBackendWithFiniteTimeouts|CreateInputProbeWithFiniteTimeout)\s*\('
    r'|\b(?:HelpProcessCapture\s*\.\s*CaptureAsync|CliProgram\s*\.\s*RunAsync)\s*\('
)


def allowed_process_construction(path, tail):
    if path == fixture:
        # 実プロセスを生成できる共通入口では無限プロファイルを明示する。
        return bool(re.match(r'[^;{]*?\bnew\s*\(\s*(?:PreviewTimeouts\s*\.\s*Infinite\b|timeouts\b)',
                             tail, re.S))
    if path == validation:
        # 公開境界の不正引数を確かめる式は生成前に例外を返す。
        return bool(re.match(r'new\s+(?:ServerBackend\s*\(\s*(?:ready|probe|cancel|stop)Timeout\s*:\s*infinite'
                             r'|InputProbe\s*\(\s*timeout\s*:\s*(?:infinite|TimeSpan\.Zero))\s*\)', tail))
    if path == server_deadlines:
        return bool(re.match(r'new\s+ServerBackend\s*\(\s*timeouts\s*,\s*clock\s*,\s*startProcess\s*:', tail))
    return False


def allowed_product_deadline(path, code, match):
    expression = match.group()
    tail = code[match.end():]
    if re.fullmatch(r'PreviewTimeouts\s*\.\s*Default', expression):
        return path == validation and bool(re.match(r'\s*\)\s*\)\s*;', tail))
    if re.fullmatch(r'PreviewTimeouts\s*\.\s*Infinite\s+with', expression):
        prefix = code[:match.start()]
        if path == server_deadlines:
            return bool(re.search(r'new\s+Sessions\s*\(\s*clock\s*,\s*$', prefix))
        if path == core_tests / 'InputProbeTests.cs':
            return bool(re.search(r'TestProcesses\.CreateDurationProbe\s*\(\s*$', prefix)
                        and re.match(r'\s*\{[^{}]*\}\s*,\s*clock\s*,', tail))
        if path == vm_shutdown:
            return bool(re.search(r'var\s+clock\s*=\s*new\s+VmShutdownClock\s*\(\s*(?:vmDeadline\s*)?\)'
                                  r'[^{}]*(?:\{[^{}]*\}[^{}]*)*timeouts\s*:\s*$', prefix)
                        and re.match(r'\s*\{\s*(?:(?:DisposeGrace|ControllerDispose)\s*=\s*TimeSpan\s*\.\s*FromSeconds\s*\(\s*\d+\s*\)\s*,\s*)*'
                                     r'ViewModelDispose\s*=\s*vmDeadline\s*,?\s*\}', tail))
        if path == fixture:
            return bool(re.match(r'\s*\{[^{}]*\}\s*,\s*clock\s*,\s*backend\s*:\s*backend\s*,'
                                 r'\s*startDurationProcess\s*:\s*\(_,\s*_\)\s*=>\s*throw\b', tail))
        return False
    if re.fullmatch(r'CliProgram\s*\.\s*RunAsync\s*\(', expression):
        # 通常の試験入口は無限プロファイルを明示する。引数配列中の文字列は除去済み。
        return bool(re.match(r'\s*(?:\w+|\[[^\]]*\])\s*,\s*\w+\s*,\s*PreviewTimeouts\s*\.\s*Infinite\b', tail))
    if re.fullmatch(r'HelpProcessCapture\s*\.\s*CaptureAsync\s*\(', expression):
        if path != cli_capture:
            return False
        return bool(re.match(r'\s*(?:\(\)\s*=>\s*child\s*,\s*clock\s*,'
                             r'|\(\)\s*=>\s*throw\s+new\s+InvalidOperationException\s*\(\s*\)\s*,'
                             r'\s*TimeProvider\.System\s*,\s*Timeout\.InfiniteTimeSpan\s*,\s*Timeout\.InfiniteTimeSpan'
                             r'|start\s*,\s*TimeProvider\.System\s*,\s*PreviewTimeouts\.Infinite\.CliHelp'
                             r'\s*,\s*PreviewTimeouts\.Infinite\.HelpCleanup)', tail))
    return False


cancel_source = r'(?:(?:global|\w+)\s*::\s*)?(?:\w+\s*\.\s*)*CancellationTokenSource'
timed_calls = (
    ('廃止した時間付き待機', re.compile(r'\b(?:WaitWithDiagnosticsAsync|TestTimeouts)\b')),
    ('時間付き取消', re.compile(
        r'\bCancelAfter\s*\('
        r'|\bnew\s+' + cancel_source + r'\s*\(\s*(?=[^)\s])'
        r'|\bCancellationTokenSource\??\s+\w+\s*(?:=|=>|\{[^{}]*\}\s*=|\([^()]*\)\s*=>)'
        r'\s*new\s*\(\s*(?=[^)\s])'
    )),
    ('時間付きのプロセス終了待ち', re.compile(r'\.\s*WaitForExit\s*\(\s*(?=[^)\s])')),
)
wait_async = re.compile(r'\.\s*WaitAsync\s*\(')
argument_delimiter = re.compile(r'[()\[\]{},]')


def call_arguments(code, opening):
    start = opening
    depth = 0
    arguments = []
    for delimiter in argument_delimiter.finditer(code, opening):
        char = delimiter.group()
        if char in '([{':
            depth += 1
        elif char in ')]}':
            if depth == 0:
                if code[start:delimiter.start()].strip():
                    arguments.append(code[start:delimiter.start()].strip())
                return arguments
            depth -= 1
        elif depth == 0:
            arguments.append(code[start:delimiter.start()].strip())
            start = delimiter.end()
    return arguments


def is_cancellation_token(code, argument):
    argument = re.sub(r'^cancellationToken\s*:\s*', '', argument)
    if re.fullmatch(r'CancellationToken\s*\.\s*None|default\s*\(\s*CancellationToken\s*\)', argument):
        return True
    if re.fullmatch(r'[\w.]+\s*\.\s*Token', argument):
        return True
    if re.fullmatch(r'\w+', argument):
        return bool(re.search(r'\bCancellationToken\s+' + re.escape(argument) + r'\b', code))
    # 条件式の各分岐がそれぞれトークンなら期限は使わない。
    conditional = re.fullmatch(r'[^?]+\?\s*([^:]+?)\s*:\s*(.+)', argument, re.S)
    return bool(conditional and all(is_cancellation_token(code, part) for part in conditional.groups()))


def is_fake_clock(code, argument):
    argument = re.sub(r'^timeProvider\s*:\s*', '', argument)
    if re.fullmatch(r'new\s+FakeTimeProvider\s*\(\s*\)', argument):
        return True
    if not re.fullmatch(r'\w+', argument):
        return False
    name = re.escape(argument)
    return bool(re.search(r'\b(?:FakeTimeProvider\s+' + name + r'\b|(?:var|TimeProvider)\s+' + name
                          + r'\s*=\s*new\s+FakeTimeProvider\s*\()', code))


def time_wait_findings(code):
    for label, pattern in timed_calls:
        for match in pattern.finditer(code):
            yield label, match
    for match in wait_async.finditer(code):
        arguments = call_arguments(code, match.end())
        if not arguments or (len(arguments) == 1 and is_cancellation_token(code, arguments[0])):
            continue
        if len(arguments) in (2, 3) and is_fake_clock(code, arguments[1]):
            continue
        yield '時間付きの非同期待機', match


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
    deadline_examples = [
        (core_tests / 'UnrelatedTests.cs', 'Run(PreviewTimeouts.Default);', False),
        (core_tests / 'UnrelatedTests.cs', 'Run(PreviewTimeouts.Infinite with { Ready = timeout });', False),
        (core_tests / 'UnrelatedTests.cs', 'var p = new PreviewTimeouts { Ready = timeout };', False),
        (core_tests / 'UnrelatedTests.cs', 'TestProcesses.CreateServerBackendWithFiniteTimeouts();', False),
        (core_tests / 'CliTests.cs', 'await CliProgram.RunAsync(args, output);', False),
        (core_tests / 'CliTests.cs', 'await CliProgram.RunAsync(args, output, PreviewTimeouts.Infinite);', True),
        (cli_capture, 'HelpProcessCapture.CaptureAsync(start, TimeProvider.System, finite, cleanup);', False),
        (cli_capture, 'HelpProcessCapture.CaptureAsync(() => child, clock, timeout, cleanup);', True),
        (server_deadlines, 'new Sessions(clock, PreviewTimeouts.Infinite with { Ready = timeout });', True),
        (server_deadlines, 'Run(PreviewTimeouts.Infinite with { Ready = timeout });', False),
        (vm_shutdown, 'var clock = new VmShutdownClock(vmDeadline); Create(clock, timeouts: PreviewTimeouts.Infinite with { ViewModelDispose = vmDeadline });', True),
        (vm_shutdown, 'var clock = TimeProvider.System; Create(clock, timeouts: PreviewTimeouts.Infinite with { ViewModelDispose = vmDeadline });', False),
        (core_tests / 'InputProbeTests.cs', 'TestProcesses.CreateDurationProbe(PreviewTimeouts.Infinite with { Input = timeout }, clock, backend, create);', True),
        (fixture, 'new(PreviewTimeouts.Infinite with { Input = timeout }, TimeProvider.System);', False),
        (fixture, 'new(PreviewTimeouts.Infinite with { Input = timeout }, clock, backend: backend, startDurationProcess: (_, _) => throw Bad());', True),
        (core_tests / 'UnrelatedTests.cs', 'Run(new global::HWEncVppPreview.Core.PreviewTimeouts());', False),
    ]
    for file, sample, allowed in deadline_examples:
        code = code_only(sample)
        match = finite_product.search(code)
        if match is None or allowed_product_deadline(file, code, match) != allowed:
            raise SystemExit('製品期限の検査に失敗しました: ' + sample)
    for sample in ('TestProcesses.CreateInputProbe()', 'ProcessSupport.Start(path, args, null)',
                   'TestProcesses.CreateServerBackend()', 'CaptureUntilExitAsync(info)'):
        if not real_process.search(sample):
            raise SystemExit('実プロセスの起動を検出できません: ' + sample)
    timed_examples = [
        'await task.WaitWithDiagnosticsAsync(delay);',
        'await current.WaitAsync(TimeSpan.FromSeconds(1));',
        'await task.WaitAsync(timeout);',
        'await task.WaitAsync(timeout: delay);',
        'await gate.WaitAsync(500, token);',
        'await task.WaitAsync(delay, TimeProvider.System, token);',
        'await task\n .WaitAsync(\n GetDelay(1, 2), cancellationToken);',
        'var guard = new global::System.Threading.CancellationTokenSource(delay, TimeProvider.System);',
        'CancellationTokenSource guard = new(500);',
        'CancellationTokenSource Guard { get; } = new(delay);',
        'source.CancelAfter(delay);',
        'process.WaitForExit(ms);',
        'process\n .WaitForExit(\n (int)delay.TotalMilliseconds);',
        'var limit = TestTimeouts.Hang;',
    ]
    for example in timed_examples:
        if not list(time_wait_findings(code_only(example))):
            raise SystemExit('試験の期限を検出できません: ' + example)
    unlimited_examples = [
        'await task;',
        'await gate.WaitAsync();',
        'await task.WaitAsync(CancellationToken.None);',
        'void Run(CancellationToken token) { task.WaitAsync(cancellationToken: token); }',
        'await task.WaitAsync(cancel.Token);',
        'void Run(CancellationToken token) { task.WaitAsync(allow ? token : CancellationToken.None); }',
        'var clock = new FakeTimeProvider(); await task.WaitAsync(delay, clock);',
        'var clock = new FakeTimeProvider(); await task.WaitAsync(timeout: delay, timeProvider: clock);',
        'FakeTimeProvider clock = new(); await task.WaitAsync(delay, clock, cancel.Token);',
        'var elapsed = TimeSpan.FromSeconds(5);',
        'var guard = new CancellationTokenSource();',
        'CancellationTokenSource guard = new();',
        'await process.WaitForExitAsync(cancel.Token);',
        'process.WaitForExit();',
        'const string source = "task.WaitWithDiagnosticsAsync(delay)";',
        '// process.WaitForExit(500);',
    ]
    for example in unlimited_examples:
        if list(time_wait_findings(code_only(example))):
            raise SystemExit('期限のない待機を誤検出しました: ' + example)
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
    code = code_only(source)
    for name, match in time_wait_findings(code):
        number = source.count('\n', 0, match.start()) + 1
        issues.append((relative, number, name, source.splitlines()[number - 1].strip()))
    for match in direct_process.finditer(code):
        if not allowed_process_construction(relative, code[match.start():]):
            number = source.count('\n', 0, match.start()) + 1
            issues.append((relative, number, 'プロセスの直接構築', 'TestProcessesの作成補助を使ってください'))
    for match in finite_product.finditer(code):
        if not allowed_product_deadline(relative, code, match):
            number = source.count('\n', 0, match.start()) + 1
            issues.append((relative, number, '実時間の製品期限', match.group().replace('\n', ' ')))
    # setterへの反射は行をまたぐことがあるため、行単位の検査とは別に拾う。
    for match in re.finditer(r'\.GetProperty\s*\([^\n]*\)\s*!?\s*\.SetValue\s*\(', source):
        number = source.count('\n', 0, match.start()) + 1
        issues.append((path.relative_to(root), number, 'setterへの反射', match.group().replace('\n', ' ')))
    method = re.compile(r'\b(?:public|private|internal)\s+(?:static\s+)?(?:async\s+)?(?:Task(?:<[^>]+>)?|void)\s+(\w+)\s*\([^)]*\)\s*\{')
    for match in method.finditer(code):
        depth, end = 1, match.end()
        while end < len(source) and depth:
            if code[end] == '{': depth += 1
            elif code[end] == '}': depth -= 1
            end += 1
        body = code[match.end():end]
        if fake_clock.search(body) and real_process.search(body):
            number = source.count('\n', 0, match.start()) + 1
            issues.append((path.relative_to(root), number, '偽時計と実プロセスの直接混在', match.group(1)))
for path, number, name, line in issues:
    print(f'{path}:{number}: {name}: {line}')
print(f'違反候補: {len(issues)}件')
sys.exit(bool(issues))
