namespace Tsuki.Engine.Stage;

/// <summary>
/// The colour grade a layer's picture is built with (<c>_STEC</c> mode, <c>_SNX2</c> brightness, <c>_SNX3</c> contrast and the colour of mode 1;
/// docs/formats/compositing.md 9): the CPU recolour 0xD36F0 runs over the decoded pixels when the texture is built.
/// </summary>
/// <param name="Mode">The STEC id 1..81 (0 = no recolour).</param>
/// <param name="Brightness">SNX2 value (i16).</param>
/// <param name="Contrast">SNX3 value (i8).</param>
/// <param name="Rgb">0xRRGGBB, used by mode 1 (the solid fill) only; 0 for every other mode.</param>
public readonly record struct ColourGrade(int Mode, int Brightness, int Contrast, int Rgb)
{
    /// <summary>True when the grade changes nothing.</summary>
    public bool IsNone => Mode == 0 && Brightness == 0 && Contrast == 0;

    /// <summary>
    /// Builds the grade from a record's fields. Modes outside 1..81 do not recolour; a mode other than 1 ignores the colour. The global colour
    /// mode word (scene +2: 2 = grey, 3 = sepia) overrides the mode (0x4BB50).
    /// </summary>
    public static ColourGrade From(int mode, int brightness, int contrast, int rgb, int globalColourMode = 0)
    {
        if (globalColourMode is 2 or 3)
        {
            mode = globalColourMode;
        }

        if (mode is < 1 or > 81)
        {
            mode = 0;
        }

        return new ColourGrade(mode, brightness, contrast, mode == 1 ? rgb & 0xFFFFFF : 0);
    }
}

/// <summary>
/// The table that gives a <see cref="ColourGrade"/> an integer key for <c>TextureRef.D</c> (the colour-effect key of a picture tile). Key 0 is "no
/// grade". The table only grows, is process-wide and thread-safe; the texture cache resolves a key with <see cref="Resolve"/> when it builds the
/// graded texture (docs/formats/compositing.md 9).
/// </summary>
public static class GradeKeys
{
    private static readonly object Gate = new();
    private static readonly List<ColourGrade> Grades = [];
    private static readonly Dictionary<ColourGrade, int> Keys = [];

    /// <summary>The key of <paramref name="grade"/> (0 for a grade that changes nothing).</summary>
    public static int Intern(in ColourGrade grade)
    {
        if (grade.IsNone)
        {
            return 0;
        }

        lock (Gate)
        {
            if (!Keys.TryGetValue(grade, out int key))
            {
                Grades.Add(grade);
                key = Grades.Count;
                Keys[grade] = key;
            }

            return key;
        }
    }

    /// <summary>The grade of a key; <see cref="ColourGrade"/> default (none) for key 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The key was never issued.</exception>
    public static ColourGrade Resolve(int key)
    {
        if (key == 0)
        {
            return default;
        }

        lock (Gate)
        {
            if (key < 0 || key > Grades.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(key), key, "no such colour-grade key");
            }

            return Grades[key - 1];
        }
    }
}
