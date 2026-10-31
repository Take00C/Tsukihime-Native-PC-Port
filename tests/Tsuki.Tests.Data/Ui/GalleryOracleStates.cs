using Tsuki.Ui;

namespace Tsuki.Tests.Data.Ui;

/// <summary>
/// The seven deterministic progress states of <c>gen_gallery_oracle.py</c>, built the same way on the C# side: the hash and every loop bound are the Python's, so
/// <see cref="GalleryOracleExpected"/> holds the oracle's answers for exactly these bits.
/// </summary>
internal static class GalleryOracleStates
{
    public static readonly string[] Names = ["empty", "sparse", "sparse_all_cg", "only_qa25", "seen_prefix", "edges_only", "everything"];

    // the 12-bit hash of the generator: ((i * 2654435761 + salt * 40503) & 0xFFFFFFFF) >> 20
    public static int Hash(int i, int salt) => (int)((uint)(((ulong)i * 2654435761UL) + ((ulong)salt * 40503UL)) >> 20);

    public static PreviewProgress Build(string name)
    {
        var p = new PreviewProgress();
        void SeenAll()
        {
            for (int n = 0; n < 576; n++)
            {
                p.Seen.Add(n);
            }
        }

        switch (name)
        {
            case "empty":
                break;
            case "sparse":
            case "sparse_all_cg":
                for (int n = 0; n < 576; n++)
                {
                    if (Hash(n, 1) < 2048)
                    {
                        p.Seen.Add(n);
                    }

                    if (Hash(n, 2) < 1500)
                    {
                        p.Done.Add(n);
                    }
                }

                for (int id = 564; id < 1268; id++)
                {
                    if (Hash(id, 3) < 1800)
                    {
                        p.Edges.Add(id);
                    }
                }

                for (int i = 0; i < 3200; i++)
                {
                    if (Hash(i, 4) < 2000)
                    {
                        p.Pictures.Add(i);
                    }
                }

                for (int t = 0; t < 0x208; t++)
                {
                    if (Hash(t, 5) < 1500)
                    {
                        p.Bgm.Add(t);
                    }
                }

                for (int b = 0; b < 16; b++)
                {
                    if (Hash(b, 6) < 2048)
                    {
                        p.Movies.Add(b);
                    }
                }

                if (name == "sparse_all_cg")
                {
                    SeenAll();
                    p.Done.Add(557);
                }

                break;
            case "only_qa25":
                p.Done.Add(557);
                break;
            case "seen_prefix":
                SeenAll();
                for (int n = 1; n <= 100; n++)
                {
                    p.Done.Add(n);
                }

                break;
            case "edges_only":
                for (int id = 564; id < 1268; id++)
                {
                    p.Edges.Add(id);
                }

                break;
            case "everything":
                for (int n = 0; n < 576; n++)
                {
                    p.Seen.Add(n);
                    p.Done.Add(n);
                }

                for (int id = 564; id < 1268; id++)
                {
                    p.Edges.Add(id);
                }

                for (int i = 0; i < 3200; i++)
                {
                    p.Pictures.Add(i);
                }

                for (int t = 0; t < 0x208; t++)
                {
                    p.Bgm.Add(t);
                }

                for (int b = 0; b < 16; b++)
                {
                    p.Movies.Add(b);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }

        return p;
    }
}
