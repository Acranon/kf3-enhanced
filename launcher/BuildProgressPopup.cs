using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace KfIII.Launcher;

/// <summary>
/// What the player sees while the game is built. Not closable: there is no game to
/// fall back to yet. A failure replaces the progress with the error and stays up, as
/// the only place the player can be told what went wrong.
/// </summary>
sealed class BuildProgressPopup : Popup
{
    protected override string TitleKey => "kf3.build.title";
    protected override Vector2 Size => new(560f, 0f);
    protected override bool Closable => false;

    public string Status = "";
    public string? Error;
    public int Step;
    public int Steps = 2;

    protected override void DrawContent()
    {
        if (Error is { } error)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.45f, 0.4f, 1f));
            ImGui.TextWrapped(Localization.T("kf3.build.failed"));
            ImGui.PopStyleColor();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextWrapped(error);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextWrapped(Localization.T("kf3.build.log"));
            return;
        }

        ImGui.TextWrapped(Localization.T("kf3.build.explain"));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text($"{Localization.T(Status)}{Dots()}");
        ImGui.Spacing();
        ImGui.ProgressBar(Steps <= 0 ? 0f : Math.Clamp(Step / (float)Steps, 0f, 1f), new Vector2(-1f, 0f), $"{Step}/{Steps}");
    }

    /// <summary>An animated ellipsis: neither step can report its own progress, so
    /// something has to show the process is alive.</summary>
    static string Dots() => new('.', 1 + (int)(ImGui.GetTime() * 2.0) % 3);

    public const string Strings = """
    {
      "strings": {
        "disc.title": { "en": "Choose your disc image" },
        "disc.description": { "en": "King's Field III Enhanced is built from your own copy of the game: the North American King's Field II (SLUS-00255), as a .cue (with its .bin) or a .chd. You only need to do this once." },
        "kf3.build.title": { "en": "Preparing King's Field III" },
        "kf3.build.explain": { "en": "The game is being built from your disc. This happens once, and again only after an update; later launches start straight away." },
        "kf3.build.translating": { "en": "Translating the game's code" },
        "kf3.build.compiling": { "en": "Compiling (about a minute)" },
        "kf3.build.failed": { "en": "The game could not be built." },
        "kf3.build.log": { "en": "The full report is in build.log, in %LOCALAPPDATA%\\kf3-enhanced." }
      }
    }
    """;
}
