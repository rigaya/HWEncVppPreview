using System.Globalization;
using System.Text.Json;

namespace HWEncParamViewer.Testing;

/// <summary>PID別Fakeログを読み、旧binの共有baseファイルも同じ入口で扱う。</summary>
internal static class FakeLogFiles
{
    internal static bool Exists(string basePath) => Paths(basePath).Any();

    internal static string[] ReadAllLines(string basePath)
    {
        var entries = new List<Entry>();
        int order = 0;
        foreach (string path in Paths(basePath))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            // 追記途中の最終行は次の読取へ回す。完了した不正JSON行は隠さず呼出元へ通知する。
            foreach (string line in reader.ReadToEnd().Split('\n').SkipLast(1).Select(line => line.TrimEnd('\r')))
            {
                if (line.Length == 0) continue;
                long timestamp = 0, sequence = order;
                int pid = 0;
                if (line.StartsWith('{'))
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("_fakeLogTimestamp", out var time)) timestamp = time.GetInt64();
                    if (root.TryGetProperty("_fakeLogPid", out var process)) pid = process.GetInt32();
                    if (root.TryGetProperty("_fakeLogSequence", out var number)) sequence = number.GetInt64();
                }
                entries.Add(new(line, timestamp, pid, sequence, order++));
            }
        }
        // PID内はsequenceを優先し、その列の先頭同士を時刻/PIDでマージする。
        // legacyの列はPID=0として元ファイルの順序を維持する。
        var pending = new PriorityQueue<Queue<Entry>, (long Timestamp, int Pid, long Sequence, int Order)>();
        foreach (var group in entries.GroupBy(entry => entry.Pid))
        {
            var queue = new Queue<Entry>(group.OrderBy(entry => entry.Sequence).ThenBy(entry => entry.Order));
            Enqueue(queue);
        }
        var result = new List<string>(entries.Count);
        while (pending.TryDequeue(out var queue, out _))
        {
            result.Add(queue.Dequeue().Line);
            if (queue.Count > 0) Enqueue(queue);
        }
        return result.ToArray();

        void Enqueue(Queue<Entry> queue)
        {
            var first = queue.Peek();
            pending.Enqueue(queue, (first.Timestamp, first.Pid, first.Sequence, first.Order));
        }
    }

    // baseを使う既存テストでも、今回作成したPID別ログを残さない。
    internal static void Delete(string basePath)
    {
        foreach (string path in Paths(basePath)) File.Delete(path);
    }

    private static IEnumerable<string> Paths(string basePath)
    {
        if (File.Exists(basePath)) yield return basePath;
        string fullPath = Path.GetFullPath(basePath);
        string directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory)) yield break;
        string prefix = Path.GetFileNameWithoutExtension(fullPath) + ".";
        string extension = Path.GetExtension(fullPath);
        foreach (string path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(extension, StringComparison.Ordinal)) continue;
            int length = name.Length - prefix.Length - extension.Length;
            if (length <= 0) continue;
            if (int.TryParse(name.AsSpan(prefix.Length, length), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0)
                yield return path;
        }
    }

    private sealed record Entry(string Line, long Timestamp, int Pid, long Sequence, int Order);
}
