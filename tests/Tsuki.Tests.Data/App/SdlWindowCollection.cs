namespace Tsuki.Tests.Data.App;

/// <summary>
/// The test classes that open a window run one after the other. <c>SDL_InitSubSystem</c> / <c>SDL_QuitSubSystem</c> are not thread-safe: two classes that create or
/// dispose a window at the same moment can leave the video subsystem uninitialised under one of them ("SDL_GL_SetAttribute: Video subsystem has not been
/// initialized", seen once in the full parallel run, package K10). The collection does not stop them from running beside tests that use no window.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SdlWindowCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "SDL windows, one test class at a time";
}
