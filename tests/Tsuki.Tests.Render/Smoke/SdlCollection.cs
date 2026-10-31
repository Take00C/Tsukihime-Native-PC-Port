namespace Tsuki.Tests.Render.Smoke;

/// <summary>
/// Tests that touch SDL (windows, GL contexts, the global event queue, virtual gamepads). SDL's
/// event queue is process-wide and its init is not thread-safe, so these tests never run in
/// parallel with each other or with any other collection. Every Tsuki.Tests.Render test that
/// creates a window or pumps events should join this collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SdlCollection
{
    /// <summary>The collection name for <c>[Collection(SdlCollection.Name)]</c>.</summary>
    public const string Name = "SDL, one test at a time";
}
