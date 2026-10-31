namespace Tsuki.Tests.Render;

// Render tests (ARCHITECTURE 6, 10.2 T5 (b), 10.5): the GPU executor against tsuki.compose renders
// on synthetic display lists, on the local GPU. Stream D adds them with the display-list executor.
// They need a GL 3.3 context; they never skip for lack of one.
internal static class RenderTestsInfo
{
}
