using System.Collections.Immutable;
using Tsuki.Data.Containers;

namespace Tsuki.Data.Images;

/// <summary>
/// Finds the image MZPs inside a container the way <c>oracle.image.census</c> does (<c>tsuki.sniff.classify_mzp</c>): a container
/// that is an image is reported, any other container is searched through its nested sections. An allpac3 entry is a set whose
/// sections are the pictures; a font file is a list of pages whose sections hold the pictures.
/// </summary>
public static class PictureWalker
{
    /// <summary>
    /// Calls <paramref name="visit"/> for every image MZP in <paramref name="container"/> in section order, with the
    /// section path from <paramref name="container"/> to the image (empty when the container itself is the image).
    /// </summary>
    public static void Walk(MzpContainer container, Action<ImmutableArray<int>, MzpContainer> visit) =>
        Walk(container, [], visit);

    private static void Walk(MzpContainer container, ImmutableArray<int> path, Action<ImmutableArray<int>, MzpContainer> visit)
    {
        if (ImageMzp.IsImage(container))
        {
            visit(path, container);
            return;
        }

        for (int i = 0; i < container.Count; i++)
        {
            if (container.IsNested(i))
            {
                Walk(container.Child(i), path.Add(i), visit);
            }
        }
    }

    /// <summary>
    /// The label the oracle gives a picture: <c>source#entry</c> and <c>/section</c> for every nested level
    /// (<c>allpac0#12</c>, <c>allpac3#5/17</c>, <c>ft_en.mrg#4/1</c>).
    /// </summary>
    public static string Label(string source, int entry, ImmutableArray<int> path)
    {
        var sb = new System.Text.StringBuilder(source.Length + 12);
        sb.Append(source).Append('#').Append(entry);
        foreach (int p in path)
        {
            sb.Append('/').Append(p);
        }

        return sb.ToString();
    }
}
