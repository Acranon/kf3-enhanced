using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using Recompiled;

// Entry point for the King's Field III port (released in North America as King's
// Field II, SLUS-00255). Hand-written, so the recompiler does not generate one into
// generated/.

// An unhandled exception goes to crash.log in the working directory (the data
// folder, for a release) before the process dies; console output redirected to a
// file has been seen to lose it.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try
    {
        File.WriteAllText("crash.log",
            $"{DateTime.Now:O}\n{e.ExceptionObject}\n");
    }
    catch (Exception) { }   // best effort: the process is going down anyway
};

// PSY-Q's interrupt-callback table, per executable (scripts/research/irq_table.py).
// InterruptCallback is a thunk through libapi's driver table; the routine in slot 2
// indexes this table with irq*4 and keeps the enable mask 11 slots past it. The
// runtime cannot derive it from HookEntryInt's jmp_buf, so it is bound here as each
// executable loads.
Event.AddListener<OverlayLoadedEvent>(e =>
{
    uint table = e.Name switch
    {
        "open" => 0x8003E948u,
        "game" => 0x8009AF9Cu,
        "end" => 0x80035A10u,
        _ => 0u,   // FDAT.T area modules share GAME.EXE's libapi
    };

    // Area modules live at 0x801E8308, outside the executables' range, so an
    // executable swap does not drop them by itself.
    if (e.Name is "open" or "end")
    {
        foreach (var n in RecompOne.Runtime.Dispatch.Dispatcher.ActiveNames)
            if (n.StartsWith("fdat", StringComparison.Ordinal))
                RecompOne.Runtime.Dispatch.Dispatcher.Unload(n);
    }

    if (table == 0) return;
    RecompOne.Runtime.Interrupts.CallbackTable = table;
    Console.WriteLine($"[KFIII] irq callback table: {e.Name} 0x{table:X8}");
});

// 8 MB, the dev-kit size: patches/Core/Pools.cs puts the primitive pools above 2 MB.
var memory = new PSMemory(MemoryMap.DevkitRamSize);
// Test tools first, so the scripted pad's load listener runs before the patches' ones.
KfIII.TestHarness.Install(() => memory);

KfIII.PortDefaults.Apply();

// Controls.Configure sets the key layout before the runtime loads settings.json,
// which happens inside Entry.Run. A release launcher has loaded it already (to ask
// for the disc); then the layout arrives through Controls.Install's migration of
// stock keys when the runtime is ready.
KfIII.Controls.Configure();
// Install order is also the order their pad and frame listeners run in; keep it.
KfIII.Controls.Install();
KfIII.Jump.Install();
KfIII.Automap.Install();
KfIII.KeyHints.Install();
KfIII.ControlsPage.Install();
KfIII.AudioRecord.Install();     // debug: KFIII_WAV, and AreaTrace's area-load traces
KfIII.AutoKey.Install();
KfIII.StatProgress.Install();
KfIII.DamageLog.Install();
KfIII.Teleport.Install();        // Debug menu > Teleport to area
KfIII.GearSpecial.Install();
KfIII.MenuMouse.Install();
KfIII.SaveState.Install();
KfIII.Visuals.Install();
KfIII.Hud.Install();
KfIII.GpuCapture.Install();      // debug: KFIII_GPU_CAPTURE

// KFIII_VSYNC=block | grid: which vblank timeline the runtime runs (grid by default).
RecompOne.Runtime.Sdk.LibEtc.BlockingVSync = KfIII.DevSwitch.Is("KFIII_VSYNC", "block");
Console.WriteLine($"[KFIII] vsync timeline: {(RecompOne.Runtime.Sdk.LibEtc.BlockingVSync ? "block" : "grid")}");

// System > Hard reset throws HardResetSignal out of the game's next VSync. The
// RecompOne runtime catches it in Runtime.Run(boot), which this port does not use
// (see PresentFrameCore), so it is caught here: reset the runtime, give the game a
// fresh machine, boot again.
while (true)
{
    try
    {
        Entry.Run(memory, args.Length > 0 ? args[0] : null, KfIII.PortDefaults.Title);
        break;
    }
    catch (RecompOne.Runtime.HardResetSignal)
    {
        Console.WriteLine("[KFIII] hard reset, game restarting");
        RecompOne.Runtime.Runtime.ResetForBoot();
        memory = new PSMemory(MemoryMap.DevkitRamSize);
    }
}
return 0;
