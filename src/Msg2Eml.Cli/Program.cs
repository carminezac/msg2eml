using System.Reflection;
using Msg2Eml.Core;

namespace Msg2Eml.Cli;

/// <summary>
/// Command-line converter: no server, no port, no runtime to install. Same conversion
/// core as the web service.
/// </summary>
public static class Program
{
    private const string Usage = """
        msg2eml — convert Outlook .msg files to .eml

        usage: msg2eml [options] <file.msg | directory>...

          -o, --output <path>   output directory (default: next to each input);
                                with a single input, a path ending in .eml is the output file
          -r, --recursive       descend into directories
          -f, --force           overwrite existing .eml files
          -q, --quiet           print errors only
          -v, --verbose         print conversion details for each file
              --stdout          write the EML of a single input to standard output
              --max-depth <n>   maximum nesting of embedded .msg (default 5)
              --max-bytes <n>   maximum decoded bytes per message (default 268435456)
              --version         print the version
          -h, --help            this text

        exit codes: 0 all converted, 1 at least one failure, 2 usage error
        """;

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error, Console.OpenStandardOutput);

    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, Func<Stream> rawStdout)
    {
        string? output = null;
        bool recursive = false, force = false, quiet = false, verbose = false, toStdout = false;
        var options = new ConversionOptions();
        var inputs = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new UsageException($"missing value for {a}");
                return args[++i];
            }
            switch (a)
            {
                case "-h" or "--help": stdout.WriteLine(Usage); return 0;
                case "--version": stdout.WriteLine(Version); return 0;
                case "-o" or "--output": output = Next(); break;
                case "-r" or "--recursive": recursive = true; break;
                case "-f" or "--force": force = true; break;
                case "-q" or "--quiet": quiet = true; break;
                case "-v" or "--verbose": verbose = true; break;
                case "--stdout": toStdout = true; break;
                case "--max-depth":
                    if (!int.TryParse(Next(), out var depth) || depth < 0) return UsageError(stderr, "--max-depth expects a non-negative integer");
                    options.MaxNestingDepth = depth; break;
                case "--max-bytes":
                    if (!long.TryParse(Next(), out var bytes) || bytes <= 0) return UsageError(stderr, "--max-bytes expects a positive integer");
                    options.MaxDecodedBytes = bytes; break;
                default:
                    if (a.StartsWith('-') && a.Length > 1) return UsageError(stderr, $"unknown option {a}");
                    inputs.Add(a); break;
            }
        }

        if (inputs.Count == 0) return UsageError(stderr, "no input given");

        List<string> files;
        try { files = Expand(inputs, recursive); }
        catch (UsageException ex) { return UsageError(stderr, ex.Message); }
        if (files.Count == 0) return UsageError(stderr, "no .msg file found");

        if (toStdout && files.Count != 1) return UsageError(stderr, "--stdout requires exactly one input file");
        var singleFileOutput = files.Count == 1 && output != null && output.EndsWith(".eml", StringComparison.OrdinalIgnoreCase);
        if (output != null && !singleFileOutput) Directory.CreateDirectory(output);

        var converter = new MsgConverter(options);
        var failures = 0;

        foreach (var file in files)
        {
            var target = toStdout ? "-" : singleFileOutput ? output! : Path.Combine(output ?? Path.GetDirectoryName(Path.GetFullPath(file))!, Path.GetFileNameWithoutExtension(file) + ".eml");

            if (!toStdout && File.Exists(target) && !force)
            {
                stderr.WriteLine($"{file}: skipped, {target} exists (use --force)");
                failures++;
                continue;
            }

            try
            {
                ConversionResult result;
                using (var input = File.OpenRead(file))
                    result = converter.Convert(input);

                if (toStdout)
                {
                    using var raw = rawStdout();
                    raw.Write(result.Eml);
                }
                else
                {
                    File.WriteAllBytes(target, result.Eml);
                }

                if (!quiet)
                {
                    var s = result.Stats;
                    stdout.WriteLine(verbose
                        ? $"{file} -> {target}  ({result.Eml.Length} B, headers={(s.HasTransportHeaders ? "transport" : "mapi")}, body={s.BodyKind}, attachments={s.Attachments}, inline={s.InlineAttachments}, nested={s.NestedMessages}{(s.UnresolvedRecipients > 0 ? $", unresolved recipients={s.UnresolvedRecipients}" : "")})"
                        : $"{file} -> {target}");
                }
            }
            catch (ConversionException ex)
            {
                failures++;
                stderr.WriteLine($"{file}: {ex.Code}: {ex.Message}");
                if (verbose && ex.InnerException != null) stderr.WriteLine($"    {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            catch (IOException ex)
            {
                failures++;
                stderr.WriteLine($"{file}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                failures++;
                stderr.WriteLine($"{file}: {ex.Message}");
            }
        }

        if (!quiet && files.Count > 1)
            stdout.WriteLine($"{files.Count - failures}/{files.Count} converted");

        return failures == 0 ? 0 : 1;
    }

    private static List<string> Expand(IEnumerable<string> inputs, bool recursive)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(input, "*.msg", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Order());
            }
            else if (File.Exists(input))
            {
                files.Add(input);
            }
            else
            {
                throw new UsageException($"not found: {input}");
            }
        }
        return files.Distinct().ToList();
    }

    private static int UsageError(TextWriter stderr, string message)
    {
        stderr.WriteLine("msg2eml: " + message);
        stderr.WriteLine("try 'msg2eml --help'");
        return 2;
    }

    internal static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev";

    private sealed class UsageException(string message) : Exception(message);
}
