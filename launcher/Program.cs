using System.Reflection;
using System.Runtime.Loader;
using KfIII;
using KfIII.Launcher;
using KfIII.Launcher.Build;
using RecompOne.Runtime;
using RecompOne.Runtime.Host.Window;

// kf3-enhanced's release entry point.
//
// The recompiled game cannot be shipped: generated/ is a translation of the game's
// code, and its compiled form is no less derived. So this program carries the inputs
// to a build (config/ and the port's own C#) and, the first time it runs, turns them
// and the player's own disc into the game:
//
//   1. move into the data folder, where every file the game writes lives
//   2. ask for the disc, accepting only King's Field III (SLUS-00255)
//   3. if this disc and this version have not been built before, recompile the
//      disc's code and compile it with the patches (about a minute, once)
//   4. load the built game and run it
//
// Adapted from Verdite2's launcher (MIT, Voicedrew11).

ConsoleAttach.ToParent();
Console.WriteLine($"[KF3Enhanced] {Ver.Full}");

try
{
    // Before anything reads a relative path: the runtime and the patches address
    // settings.json, enhancements.json, the memory cards, the quick save and the map
    // memory relatively, and an installed build's own folder is read-only.
    Paths.Prepare();

    Runtime.DiscValidator = DiscCheck.Validate;
    PortDefaults.Apply();
    Runtime.Initialize(PortDefaults.Title);
    Localization.Merge(BuildProgressPopup.Strings);

    var icon = Path.Combine(AppContext.BaseDirectory, "kf3.png");
    if (File.Exists(icon)) Runtime.SetIcon(File.ReadAllBytes(icon));

    // The runtime's picker: a native file dialog that refuses what DiscValidator
    // rejects and saves the accepted path. A player who has chosen passes straight on.
    Runtime.WaitForValidDisc();
    var discPath = Runtime.CdPath;

    // An image on the command line is the developer form. It is settled here, before
    // the build key: the build bakes the disc's sector positions, so the image that is
    // played must be the one that was built.
    if (args.Length > 0)
    {
        if (DiscCheck.Validate(args[0]) is { } problem)
            throw new InvalidOperationException($"{args[0]}: {problem}");
        discPath = args[0];
    }

    var gameDll = Path.Combine(Paths.Builds, BuildKey.Compute(discPath), GameCompile.AssemblyName + ".dll");
    if (!File.Exists(gameDll)) BuildGame(discPath, gameDll);

    Play(gameDll, discPath);
}
catch (Exception e)
{
    Console.Error.WriteLine($"[KF3Enhanced {Ver.Full}] {e}");
    try { File.WriteAllText(Paths.CrashLog, $"KF3Enhanced {Ver.Full}\n\n{e}\n"); } catch (IOException) { }
    return 1;
}

return 0;

// Builds the game on a worker thread while the main thread, which owns the window,
// keeps pumping it: a window that stops for a minute is one the desktop offers to
// close.
static void BuildGame(string discPath, string gameDll)
{
    var popup = new BuildProgressPopup();
    PopupManager.Register(popup);
    popup.Open();

    var generated = Path.Combine(Paths.Data, "generated");
    var log = new System.Text.StringBuilder();

    var work = new Thread(() =>
    {
        try
        {
            popup.Status = "kf3.build.translating";
            popup.Step = 0;
            // Never kept between builds: it is the game's code, translated.
            if (Directory.Exists(generated)) Directory.Delete(generated, recursive: true);
            Recompile.Run(discPath, generated);

            popup.Status = "kf3.build.compiling";
            popup.Step = 1;
            GameCompile.Run(generated, gameDll);
            popup.Step = 2;
        }
        catch (Exception e)
        {
            log.AppendLine(e.ToString());
            popup.Error = e.Message;
        }
        finally
        {
            try { if (Directory.Exists(generated)) Directory.Delete(generated, recursive: true); }
            catch (IOException) { }
        }
    })
    { IsBackground = true, Name = "kf3-build" };

    work.Start();
    while (work.IsAlive) Runtime.Pump();

    if (popup.Error is null)
    {
        popup.Close();
        return;
    }

    File.WriteAllText(Paths.BuildLog, $"KF3Enhanced {Ver.Full}\n\n{log}");
    // Hold the failure on screen; closing the window ends the process.
    while (true) Runtime.Pump();
}

// Hands over to the built game: its Program.cs entry point, with the disc image as
// argv[0]. Loaded into the default context because the game is the rest of the
// process's life and the runtime detours into it.
static void Play(string gameDll, string discPath)
{
    var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(gameDll);
    var main = asm.EntryPoint ?? throw new InvalidOperationException($"{gameDll} has no entry point.");

    try { main.Invoke(null, [new[] { discPath }]); }
    catch (TargetInvocationException e) when (e.InnerException is not null)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
    }
}
