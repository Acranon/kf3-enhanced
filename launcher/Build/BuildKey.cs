using System.Security.Cryptography;
using System.Text;
using RecompOne.Runtime.Cdrom;

namespace KfIII.Launcher.Build;

/// <summary>
/// What identifies one built game assembly, so a later launch can skip the build and
/// a changed input is never served a stale one. Hashed together:
///
///   - the disc's code: the files the recompiler reads (the executables and FDAT.T),
///     not the whole image, so two dumps that differ only in padding or movies share
///     a build;
///   - the shipped sources (content/src), compiled into the assembly;
///   - the shipped config (content/config), which decides where every function
///     starts and which are bound to the runtime;
///   - the launcher's version, for a change in how the build is done.
/// </summary>
static class BuildKey
{
    static readonly string[] DiscFiles =
        ["SYSTEM.CNF", DiscCheck.BootExe, "OPEN.EXE", "GAME.EXE", "END.EXE", "CD/COM/FDAT.T"];

    public static string Compute(string discPath)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, "kf3-enhanced");
        Add(hash, typeof(BuildKey).Assembly.GetName().Version?.ToString() ?? "0");

        using (var fs = DiscFs.Open(discPath))
            foreach (var file in DiscFiles)
            {
                Add(hash, file);
                try { hash.AppendData(SHA256.HashData(fs.ReadFile(file))); }
                catch (Exception) { Add(hash, "<missing>"); }
            }

        foreach (var src in Sources.All().Concat(Sources.Config()))
        {
            Add(hash, Path.GetRelativePath(Paths.Content, src).Replace('\\', '/'));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(src)));
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
    }

    /// <summary>Length-prefixed, so "ab" + "c" and "a" + "bc" hash differently.</summary>
    static void Add(IncrementalHash hash, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}

/// <summary>The shipped payload the game is built from, in a stable order.</summary>
static class Sources
{
    public static IEnumerable<string> All() => In(Paths.ContentSrc, "*.cs");
    public static IEnumerable<string> Config() => In(Paths.ContentConfig, "*.json");

    static IEnumerable<string> In(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) yield break;
        var files = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);   // not the host's locale
        foreach (var f in files) yield return f;
    }
}
