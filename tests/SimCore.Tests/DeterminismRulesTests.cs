using System.Text;
using System.Text.RegularExpressions;

namespace SimCore.Tests;

// DeterminismRulesTests fails if sim code uses anything that could make two
// machines compute different results from the same commands. It scans the
// source of every namespace that runs inside a tick. Replays, Content and
// SimRun do IO and are exempt.
public class DeterminismRulesTests
{
    // DeterministicDirs must follow the determinism rules.
    private static readonly string[] DeterministicDirs = ["FixedPoint", "Rng", "Model", "Sim", "Scenarios", "Bots"];

    // ExemptDirs may do IO; nothing in them runs inside a tick.
    private static readonly string[] ExemptDirs = ["Replays", "Content"];

    // Banned lists what can make results differ between runs or machines.
    private static readonly (Regex Pattern, string Why)[] Banned =
    [
        (new(@"\b(float|double|decimal|Single|Double|Decimal|Half|MathF)\b"), "floating point; the sim is integer-only, use Fixed"),
        (new(@"(?<![\w.])\d[\d_]*(\.\d[\d_]*([eE][+-]?\d+)?[fFdDmM]?|[eE][+-]?\d+[fFdDmM]?|[fFdDmM])\b"), "float literal; the sim is integer-only"),
        (new(@"\bMath\.(Sqrt|Cbrt|Sin|Cos|Tan|Asin|Acos|Atan2?|Sinh|Cosh|Tanh|Pow|Exp|Log(2|10)?|Round|Floor|Ceiling|Truncate|ScaleB|FusedMultiplyAdd)\b"), "float math; use Fixed"),
        (new(@"\bSystem\.Random\b|\bRandom\b|\bRandomNumberGenerator\b"), "unseeded or global randomness; use Rng.Rand"),
        (new(@"\b(DateTime|DateTimeOffset|Stopwatch|TimeProvider|TimeSpan)\b"), "wall-clock time differs between machines"),
        (new(@"\bEnvironment\b"), "the environment differs between machines"),
        (new(@"\b(Thread|ThreadPool|ThreadLocal|ThreadStatic|Task|ValueTask|Parallel|Interlocked|Volatile|Monitor|async|await)\b|\block\s*\("), "concurrency makes ordering nondeterministic; the tick runs on one thread"),
        (new(@"\b(Dictionary|HashSet|ConcurrentDictionary|Hashtable|GetHashCode|HashCode)\b"), "hash ordering is randomized per process; use lists sorted by ID"),
        (new(@"\bunsafe\b|\bSystem\.Reflection\b"), "memory layout and reflection are not needed by the sim"),
    ];

    [Fact]
    public void SourceFollowsRules()
    {
        var src = Path.Combine(TestPaths.RepoRoot(), "src", "SimCore");
        var violations = new List<string>();
        var scanned = 0;
        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, path);
            if (rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            var top = rel.Split(Path.DirectorySeparatorChar)[0];
            if (ExemptDirs.Contains(top))
            {
                continue;
            }
            // New code must land in a checked namespace, or be exempted here on purpose.
            Assert.True(DeterministicDirs.Contains(top), $"{rel}: not in a namespace the determinism rules cover");
            violations.AddRange(Check(File.ReadAllText(path)).Select(v => $"{rel}:{v}"));
            scanned++;
        }
        Assert.True(scanned >= 10, $"only {scanned} files scanned; is the source layout right?");
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Theory]
    [InlineData("var x = 1.5;")]
    [InlineData("var x = 2f;")]
    [InlineData("var x = 1e3;")]
    [InlineData("double d = 0;")]
    [InlineData("var r = Math.Sqrt(n);")]
    [InlineData("var r = MathF.Sin(n);")]
    [InlineData("var r = new System.Random();")]
    [InlineData("var t = DateTime.Now;")]
    [InlineData("var s = Stopwatch.StartNew();")]
    [InlineData("var e = Environment.TickCount;")]
    [InlineData("new Thread(Run).Start();")]
    [InlineData("Task.Run(Run);")]
    [InlineData("Parallel.For(0, n, Run);")]
    [InlineData("async void Run() { await Go(); }")]
    [InlineData("var m = new Dictionary<int, int>();")]
    [InlineData("var s = new HashSet<int>();")]
    [InlineData("var h = u.GetHashCode();")]
    [InlineData("unsafe { }")]
    [InlineData("var s = \"// not a comment\"; double d;")]
    public void CheckerCatchesViolations(string code) => Assert.NotEmpty(Check(code));

    [Theory]
    [InlineData("// a float in a comment is fine")]
    [InlineData("/* so is Dictionary<int, int> here */ var x = 1;")]
    [InlineData("/// <summary>Returns floor(sqrt(n)) without Math.Sqrt.</summary>")]
    [InlineData("var z = 0x9e3779b97f4a7c15 ^ 0xbf58476d1ce4e5b9;")]
    [InlineData("var x = Math.Abs(Math.Max(a, b)); var c = 'd';")]
    [InlineData("var n = 10_000; var u = 1UL << 40;")]
    public void CheckerAllowsCleanCode(string code) => Assert.Empty(Check(code));

    // Check returns "line: why (match)" for every banned use in source.
    private static List<string> Check(string source)
    {
        var code = StripComments(source);
        var output = new List<string>();
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var (pattern, why) in Banned)
            {
                foreach (Match m in pattern.Matches(lines[i]))
                {
                    output.Add($"{i + 1}: {why} ({m.Value})");
                }
            }
        }
        return output;
    }

    // StripComments removes // and /* */ comments, keeping newlines so line
    // numbers still match. String and char literals are kept, and skipped over
    // so a "//" inside one is not taken for a comment.
    private static string StripComments(string s)
    {
        var b = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (At(s, i, "//"))
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }
            }
            else if (At(s, i, "/*"))
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? s.Length : end + 2;
                b.Append('\n', s.AsSpan(i, end - i).Count('\n'));
                i = end;
            }
            else if (At(s, i, "\"\"\""))
            {
                var end = s.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                end = end < 0 ? s.Length : end + 3;
                b.Append(s, i, end - i);
                i = end;
            }
            else if (s[i] is '"' or '\'')
            {
                var quote = s[i];
                var verbatim = i > 0 && (s[i - 1] == '@' || (s[i - 1] == '$' && i > 1 && s[i - 2] == '@'));
                var start = i++;
                while (i < s.Length && s[i] != '\n')
                {
                    if (!verbatim && s[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (s[i] == quote)
                    {
                        if (verbatim && i + 1 < s.Length && s[i + 1] == quote)
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    i++;
                }
                i = Math.Min(i, s.Length);
                b.Append(s, start, i - start);
            }
            else
            {
                b.Append(s[i++]);
            }
        }
        return b.ToString();
    }

    private static bool At(string s, int i, string token) => string.CompareOrdinal(s, i, token, 0, token.Length) == 0;
}
