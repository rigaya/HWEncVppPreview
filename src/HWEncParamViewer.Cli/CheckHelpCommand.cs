using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.Cli;

/// <summary>画像を要求せず、helpが補完用スキーマとして全件利用できるかを調べる。</summary>
internal static class CheckHelpCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static bool IsRequested(string[] args)
    {
        // 画像引数の値として指定された同名文字列を、新しいモードだと誤認しない。
        for (int i = 0; i < args.Length; i += 2)
            if (args[i] == "--check-help") return true;
        return false;
    }

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TimeProvider clock, CancellationToken token)
    {
        var result = new Result();
        try
        {
            var options = Parse(args);
            result.SourceKind = options.Encoder is null ? "file" : "encoder";
            string text;
            if (options.Encoder is null)
                text = await File.ReadAllTextAsync(options.HelpFile!, token).WaitAsync(options.Timeout, clock, token).ConfigureAwait(false);
            else
            {
                var captured = await HelpProcessCapture.CaptureAsync(HelpProcessCapture.StartInfo(options.Encoder), clock,
                    options.Timeout, token).ConfigureAwait(false);
                result.EncoderExitCode = captured.ExitCode;
                result.StderrTail = Tail(captured.StandardError);
                if (captured.ExitCode != 0)
                    throw new IOException("helpを取得する子プロセスが異常終了しました。");
                text = string.IsNullOrWhiteSpace(captured.StandardOutput) ? captured.StandardError : captured.StandardOutput;
            }
            Validate(text, result);
        }
        catch (UsageException exception)
        { result.ExitCode = 2; result.Error = exception.Message; }
        catch (Exception exception)
        {
            result.ExitCode = 3;
            result.Error = exception.Message;
            result.TimedOut = exception is TimeoutException || exception is HelpCaptureException { TimedOut: true };
            if (exception is HelpCaptureException capture)
            {
                result.EncoderExitCode = capture.EncoderExitCode;
                result.StderrTail = Tail(capture.StandardError);
                result.CleanupError = capture.CleanupError;
            }
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
        return result.ExitCode;
    }

    private static void Validate(string text, Result result)
    {
        // Index自身の列挙を正解にせず、本文の全出現を独立に拾って切り出し漏れを検出する。
        string[] names = Regex.Matches(text, @"--vpp-[a-z0-9-]+", RegexOptions.CultureInvariant)
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var index = new HelpIndex(text);
        var schema = new HelpSchema(text);
        result.OptionCount = names.Length;
        result.MissingIndex = names.Where(name => !index.OptionNames.Contains(name, StringComparer.Ordinal)).ToArray();
        result.EmptyLookup = names.Where(name => string.IsNullOrWhiteSpace(index.Lookup(name))).ToArray();
        result.LookupWithoutName = names.Where(name => !index.Lookup(name).Contains(name, StringComparison.Ordinal)).ToArray();
        result.MissingSchema = names.Where(name => schema.Lookup(name) is null).ToArray();
        result.DegradedOptions = schema.Options.Where(option => option.IsNameOnly || option.Category is null)
            .Select(option => option.Name).Order(StringComparer.Ordinal).ToArray();
        result.DegradedParameters = schema.Options.SelectMany(option => option.Parameters
            .Where(parameter => parameter.IsNameOnly || parameter.Type is null && !parameter.IsStructured)
            .Select(parameter => new DegradedParameter(option.Name, parameter.Name))).OrderBy(value => value.Option, StringComparer.Ordinal)
            .ThenBy(value => value.Parameter, StringComparer.Ordinal).ToArray();
        result.ParameterCount = schema.Options.Sum(option => option.Parameters.Count);
        result.Valid = names.Length > 0 && result.MissingIndex.Length == 0 && result.EmptyLookup.Length == 0
            && result.LookupWithoutName.Length == 0 && result.MissingSchema.Length == 0
            && result.DegradedOptions.Length == 0 && result.DegradedParameters.Length == 0;
        result.ExitCode = result.Valid ? 0 : 1;
    }

    private static Options Parse(string[] args)
    {
        bool seen = false;
        string? encoder = null, file = null;
        TimeSpan timeout = TimeSpan.FromSeconds(5);
        bool timeoutSeen = false;
        for (int i = 0; i < args.Length; ++i)
        {
            string name = args[i];
            if (name == "--check-help")
            {
                if (seen) throw new UsageException("--check-helpは一度だけ指定してください。");
                seen = true;
                continue;
            }
            if (name is not ("--enc" or "--help-file" or "--watchdog-seconds"))
                throw new UsageException("help検査では指定できない引数です: " + name);
            if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                throw new UsageException("引数の値が必要です: " + name);
            if (name == "--enc")
            {
                if (encoder is not null) throw new UsageException("--encの重複です。");
                encoder = args[i];
            }
            else if (name == "--help-file")
            {
                if (file is not null) throw new UsageException("--help-fileの重複です。");
                file = args[i];
            }
            else
            {
                if (timeoutSeen || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    || !double.IsFinite(seconds) || seconds <= 0)
                    throw new UsageException("--watchdog-secondsには有限の正の秒数を一度だけ指定してください。");
                try { timeout = TimeSpan.FromSeconds(seconds); }
                catch (OverflowException) { throw new UsageException("help検査の期限が大きすぎます。"); }
                if (timeout <= TimeSpan.Zero) throw new UsageException("help検査の期限が短すぎます。");
                timeoutSeen = true;
            }
        }
        if (!seen || (encoder is null) == (file is null))
            throw new UsageException("--check-helpと、--encまたは--help-fileのどちらか一つを指定してください。");
        return new(encoder, file, timeout);
    }

    private static string Tail(string value) => value.Length <= 8192 ? value : value[^8192..];
    private sealed class UsageException(string message) : Exception(message);
    private sealed record Options(string? Encoder, string? HelpFile, TimeSpan Timeout);
    private sealed record DegradedParameter(string Option, string Parameter);
    private sealed class Result
    {
        public string Mode { get; } = "check-help";
        public bool Strict { get; } = true;
        public string? SourceKind { get; set; }
        public int OptionCount { get; set; }
        public int ParameterCount { get; set; }
        public string[] MissingIndex { get; set; } = [];
        public string[] EmptyLookup { get; set; } = [];
        public string[] LookupWithoutName { get; set; } = [];
        public string[] MissingSchema { get; set; } = [];
        public string[] DegradedOptions { get; set; } = [];
        public DegradedParameter[] DegradedParameters { get; set; } = [];
        public bool Valid { get; set; }
        public int ExitCode { get; set; }
        public int? EncoderExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string? Error { get; set; }
        public string StderrTail { get; set; } = "";
        public string? CleanupError { get; set; }
    }
}
