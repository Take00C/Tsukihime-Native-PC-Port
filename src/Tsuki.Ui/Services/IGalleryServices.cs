namespace Tsuki.Ui;

/// <summary>
/// The persistent progress the gallery, the music player and the flowchart read (docs/formats/gallery.md 2.1): the unlock bit sets of the system data and the
/// per-playthrough marks of the running story. Every query takes the game's own numbers and answers false outside its range.
/// </summary>
public interface IUiProgress
{
    /// <summary>scene_seen(n), n at most 0x23F (G[110..145]): the scene was started at least once (flowchart "read", the CG categories, two music tracks).</summary>
    bool SceneSeen(int n);

    /// <summary>scene_done(n), n at most 0x23F (G[300..335]): the scene was finished (flowchart nodes, the endings, every CG with node 557).</summary>
    bool SceneDone(int n);

    /// <summary>edge_seen(id), 564 to 1267 (G[400..443]): the flowchart edge or choice was taken in any playthrough.</summary>
    bool EdgeSeen(int id);

    /// <summary>movie_seen bit 0..15 (G[5]): 0 = the first opening watched, 1 = the second.</summary>
    bool MovieSeen(int bit);

    /// <summary>picture_seen(seenIndex), 0..3199: the picture block of the system data.</summary>
    bool PictureSeen(int seenIndex);

    /// <summary>bgm_heard(track): the BGM block of the system data (bit 0xC80 + track).</summary>
    bool BgmHeard(int track);

    /// <summary>RTST(n): the node was entered in this playthrough (the per-save words F[201..272]; the in-game chart lights these).</summary>
    bool RouteScene(int n);

    /// <summary>The edge was taken in this playthrough (F[600..687]).</summary>
    bool RouteEdge(int id);
}

/// <summary>
/// What the gallery (CG viewer, music player, movies, endings) and the flowchart need from the game besides <see cref="IUiProgress"/> (package K5b). The movie
/// gallery reuses the full-screen movie members of <see cref="IUiSystemServices"/> (<c>StartIntroMovie</c> and friends), the music player the BGM members
/// (<c>PlayBgm</c>, <c>FadeOutBgm</c>); this interface adds the rest.
/// </summary>
public interface IUiGalleryServices : IUiProgress
{
    /// <summary>F110: the node of the running story (the in-game chart opens on it), 0 when no node was entered.</summary>
    int CurrentNode { get; }

    /// <summary>The BGM track that plays now (bgm.hed index), or 0 when none does.</summary>
    int PlayingBgm { get; }

    /// <summary>Sets bgm_heard(track): the player marks the track of an entry that a variant or a special rule unlocked (gallery.md 6.2, 0x87630).</summary>
    void MarkBgmHeard(int track);

    /// <summary>Starts <paramref name="track"/> from its beginning at once at <paramref name="volume"/> (0..1, the player's own volume) and replaces whatever plays.</summary>
    void PlayMusic(int track, float volume);

    /// <summary>Pauses or resumes the BGM.</summary>
    void PauseMusic(bool paused);

    /// <summary>Sets the BGM volume (0..1) over a tenth of a second.</summary>
    void SetMusicVolume(float volume);

    /// <summary>Stops the BGM at once (the player's stop; the track change stops the old one the same way).</summary>
    void StopMusic();

    /// <summary>True while a gallery replay runs (F024 is set: the archive scene 3000 plays a scene of the gallery).</summary>
    bool ReplayActive { get; }

    /// <summary>
    /// Replays a scene from the gallery (docs/formats/gallery.md 3.2): the game sets G008 = <paramref name="scriptScene"/>, F024 = 1 and starts scene 3000
    /// (ARCHIVE), which jumps to the scene. The UI has closed itself; when the replay ends the host calls <see cref="UiRoot.ReturnToGallery"/>.
    /// </summary>
    /// <param name="scriptScene">The scene to start: a flowchart node's script scene (240..790, 2000..2007) or a Q and A scene (1000..1024).</param>
    /// <param name="node">The flowchart node (or scene) the replay was picked from, for the way back.</param>
    void ReplayScene(int scriptScene, int node);

    /// <summary>The system menu's FlowChart during a replay picked in the gallery's flowchart was confirmed: ends the replay, the story returns to the gallery.</summary>
    void EndReplay();

    /// <summary>
    /// The in-game flowchart's jump (docs/formats/gallery.md 5.5; knife <c>jump_record</c>): rewinds the playthrough to <paramref name="node"/> (the node bits of
    /// the later columns and the chapter words are cleared, F026 = <paramref name="scene"/> and F110 = <paramref name="node"/> are set, the kept F words survive)
    /// and starts the node's scene from its top. Returns false when no scene starts (a filler record: the chart closes back into the menu).
    /// </summary>
    /// <param name="column">The static column index (0..276) of the record.</param>
    /// <param name="node">The record's node id (its scene_done bit), 0 for a filler.</param>
    /// <param name="scene">The scene id to start (the record's script scene), -1 for a filler.</param>
    bool FlowchartJump(int column, int node, int scene);
}
