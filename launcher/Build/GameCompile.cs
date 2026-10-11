using System.Reflection.Metadata;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace KfIII.Launcher.Build;

/// <summary>
/// Compiles the recompiler's output together with the port's own C# into one game
/// assembly, with Roslyn, in process.
///
/// Together, because the patches call into the recompiled code directly
/// (Program.cs calls Recompiled.Entry.Run; patches call Recompiled.Game.kf_* and are
/// called from it as hooks), and one compilation keeps every one of those a plain
/// static call.
///
/// The options must agree with KingsFieldIII.csproj, which compiles the same sources
/// on the developer path: unsafe code, nullable on, the SDK's implicit usings. A
/// difference would be a bug that only shows in a release.
/// </summary>
static class GameCompile
{
    public const string AssemblyName = "KingsFieldIII";

    public static void Run(string generatedDir, string dllPath)
    {
        var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(SourceText.From(GlobalUsings, Encoding.UTF8), parse, "GlobalUsings.g.cs"),
        };
        foreach (var file in Directory.EnumerateFiles(generatedDir, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            trees.Add(Parse(file, parse));
        foreach (var file in Sources.All())
            trees.Add(Parse(file, parse));

        var options = new CSharpCompilationOptions(OutputKind.ConsoleApplication)
            .WithAllowUnsafe(true)
            .WithNullableContextOptions(NullableContextOptions.Enable)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithPlatform(Platform.AnyCpu)
            .WithConcurrentBuild(true)
            // The recompiled code warns freely (unreachable code after delay slots,
            // unused labels) and none of it is actionable.
            .WithGeneralDiagnosticOption(ReportDiagnostic.Suppress)
            .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
            {
                ["CS5001"] = ReportDiagnostic.Error,   // no entry point: loads, then does nothing
            });

        var compilation = CSharpCompilation.Create(AssemblyName, trees, References(), options);

        // Emit beside the destination and rename, so a failed or interrupted build
        // never leaves a half-written assembly for the next launch to load.
        var tmp = dllPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);
        EmitResult result;
        using (var fs = File.Create(tmp))
            result = compilation.Emit(fs, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));

        if (!result.Success)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw new InvalidOperationException(Report(result));
        }
        File.Move(tmp, dllPath, overwrite: true);
    }

    static SyntaxTree Parse(string file, CSharpParseOptions parse) =>
        CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(file), Encoding.UTF8), parse, file);

    static string Report(EmitResult result)
    {
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"The game did not compile ({errors.Count} error{(errors.Count == 1 ? "" : "s")}).");
        foreach (var d in errors.Take(20)) sb.AppendLine($"  {d}");
        if (errors.Count > 20) sb.AppendLine($"  ... and {errors.Count - 20} more.");
        return sb.ToString();
    }

    /// <summary>The SDK's implicit usings (ImplicitUsings is an SDK feature; Roslyn on
    /// its own adds none).</summary>
    const string GlobalUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    /// <summary>
    /// What the game compiles against: everything the host will resolve at run time
    /// (its trusted-platform list: the framework, the runtime and their dependencies),
    /// not just what happens to be loaded already.
    /// </summary>
    static unsafe List<MetadataReference> References()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa && tpa.Length > 0)
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (Skip(name) || !seen.Add(name) || !File.Exists(path)) continue;
                try { refs.Add(MetadataReference.CreateFromFile(path)); }
                catch (BadImageFormatException) { }   // a native library on the list
            }

        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic) continue;
            var name = a.GetName().Name ?? "";
            if (Skip(name) || !seen.Add(name)) continue;
            if (!string.IsNullOrEmpty(a.Location)) { refs.Add(MetadataReference.CreateFromFile(a.Location)); continue; }
            if (a.TryGetRawMetadata(out byte* blob, out int length))
                refs.Add(AssemblyMetadata.Create(ModuleMetadata.CreateFromMetadata((IntPtr)blob, length)).GetReference());
        }

        if (refs.Count == 0) throw new InvalidOperationException("No reference assemblies were found; this install is incomplete.");
        return refs;
    }

    /// <summary>
    /// The launcher's own assemblies, which the game must not compile against (the
    /// launcher also contains KfIII.PortDefaults, which the game defines itself).
    /// </summary>
    static bool Skip(string name) => name is "recompone" or "KF3Enhanced";
}
