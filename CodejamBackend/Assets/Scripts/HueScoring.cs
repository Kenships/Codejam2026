using System;
using UnityEngine;

/// <summary>
/// Scoring for guesses (from the project spec):
///
///   base   = max(0, 1000 × (1 − ΔE / 35))         ΔE = CIEDE2000 between guess and target
///   speed  = ×1.5 for an instant guess, down to ×1.0 at the end of the guessing time
///   streak = +100 for each consecutive guess with ΔE ≤ 10 after the first
///            (2nd close guess in a row +100, 3rd +200, ...)
///   artist = average of the points their drawing earned from the players who guessed it
///
/// CIEDE2000 is the standard "how different do these colours look to a person" measure:
/// ΔE ≈ 1 is barely noticeable, ≈ 5 is close, ≥ 35 scores nothing here.
/// </summary>
public static class HueScoring
{
    public const double CutoffDeltaE = 35.0;
    public const double StreakDeltaE = 10.0;
    public const int StreakBonusPerStep = 100;
    public const double MaxSpeedMultiplier = 1.5;

    // ---- points ----

    /// <summary>Points for one guess, including speed and streak bonuses.</summary>
    /// <param name="deltaE">CIEDE2000 between the guess and the target.</param>
    /// <param name="elapsedSeconds">Time from the drawing appearing to the guess arriving.</param>
    /// <param name="durationSeconds">Length of the guessing phase.</param>
    /// <param name="streakAfterThisGuess">The guesser's streak including this guess (see UpdateStreak).</param>
    public static int GuessPoints(double deltaE, double elapsedSeconds, double durationSeconds, int streakAfterThisGuess)
    {
        int basePoints = BasePoints(deltaE);
        if (basePoints == 0)
            return 0;

        return (int)Math.Round(basePoints * SpeedMultiplier(elapsedSeconds, durationSeconds))
               + StreakBonus(streakAfterThisGuess);
    }

    public static int BasePoints(double deltaE) =>
        (int)Math.Round(Math.Max(0.0, 1000.0 * (1.0 - deltaE / CutoffDeltaE)));

    public static double SpeedMultiplier(double elapsedSeconds, double durationSeconds)
    {
        if (durationSeconds <= 0)
            return 1.0;
        double progress = Math.Min(1.0, Math.Max(0.0, elapsedSeconds / durationSeconds));
        return MaxSpeedMultiplier - (MaxSpeedMultiplier - 1.0) * progress;
    }

    /// <summary>The guesser's streak after this guess: +1 if close enough, otherwise back to 0.</summary>
    public static int UpdateStreak(int streakBefore, double deltaE) =>
        deltaE <= StreakDeltaE ? streakBefore + 1 : 0;

    public static int StreakBonus(int streak) =>
        streak >= 2 ? (streak - 1) * StreakBonusPerStep : 0;

    // ---- colour difference ----

    /// <summary>CIEDE2000 colour difference between two Unity colours (sRGB, alpha ignored).</summary>
    public static double DeltaE2000(Color a, Color b) =>
        DeltaE2000Rgb(a.r, a.g, a.b, b.r, b.g, b.b);

    /// <summary>CIEDE2000 between two sRGB colours with channels in 0..1.</summary>
    public static double DeltaE2000Rgb(double r1, double g1, double b1, double r2, double g2, double b2)
    {
        RgbToLab(r1, g1, b1, out double l1, out double a1, out double bb1);
        RgbToLab(r2, g2, b2, out double l2, out double a2, out double bb2);
        return DeltaE2000Lab(l1, a1, bb1, l2, a2, bb2);
    }

    /// <summary>sRGB (0..1) → CIE L*a*b* with a D65 white point.</summary>
    public static void RgbToLab(double r, double g, double b, out double l, out double a, out double bLab)
    {
        double rl = SrgbToLinear(r), gl = SrgbToLinear(g), bl = SrgbToLinear(b);

        // Linear sRGB → XYZ (D65), scaled so white has Y = 100
        double x = (0.4124564 * rl + 0.3575761 * gl + 0.1804375 * bl) * 100.0;
        double y = (0.2126729 * rl + 0.7151522 * gl + 0.0721750 * bl) * 100.0;
        double z = (0.0193339 * rl + 0.1191920 * gl + 0.9503041 * bl) * 100.0;

        double fx = LabF(x / 95.047), fy = LabF(y / 100.0), fz = LabF(z / 108.883);
        l = 116.0 * fy - 16.0;
        a = 500.0 * (fx - fy);
        bLab = 200.0 * (fy - fz);
    }

    /// <summary>CIEDE2000 (kL = kC = kH = 1) between two L*a*b* colours.</summary>
    public static double DeltaE2000Lab(double l1, double a1, double b1, double l2, double a2, double b2)
    {
        const double deg = Math.PI / 180.0;
        double pow25To7 = Math.Pow(25.0, 7.0);

        double c1 = Math.Sqrt(a1 * a1 + b1 * b1);
        double c2 = Math.Sqrt(a2 * a2 + b2 * b2);
        double cBar7 = Math.Pow((c1 + c2) / 2.0, 7.0);
        double g = 0.5 * (1.0 - Math.Sqrt(cBar7 / (cBar7 + pow25To7)));

        double a1p = (1.0 + g) * a1, a2p = (1.0 + g) * a2;
        double c1p = Math.Sqrt(a1p * a1p + b1 * b1), c2p = Math.Sqrt(a2p * a2p + b2 * b2);
        double h1p = HueDegrees(b1, a1p), h2p = HueDegrees(b2, a2p);

        double dLp = l2 - l1;
        double dCp = c2p - c1p;

        double dhp;
        if (c1p * c2p == 0.0) dhp = 0.0;
        else if (Math.Abs(h2p - h1p) <= 180.0) dhp = h2p - h1p;
        else if (h2p - h1p > 180.0) dhp = h2p - h1p - 360.0;
        else dhp = h2p - h1p + 360.0;
        double dHp = 2.0 * Math.Sqrt(c1p * c2p) * Math.Sin(dhp * deg / 2.0);

        double lBarP = (l1 + l2) / 2.0;
        double cBarP = (c1p + c2p) / 2.0;

        double hBarP;
        if (c1p * c2p == 0.0) hBarP = h1p + h2p;
        else if (Math.Abs(h1p - h2p) <= 180.0) hBarP = (h1p + h2p) / 2.0;
        else if (h1p + h2p < 360.0) hBarP = (h1p + h2p + 360.0) / 2.0;
        else hBarP = (h1p + h2p - 360.0) / 2.0;

        double t = 1.0
                   - 0.17 * Math.Cos((hBarP - 30.0) * deg)
                   + 0.24 * Math.Cos(2.0 * hBarP * deg)
                   + 0.32 * Math.Cos((3.0 * hBarP + 6.0) * deg)
                   - 0.20 * Math.Cos((4.0 * hBarP - 63.0) * deg);

        double dTheta = 30.0 * Math.Exp(-Math.Pow((hBarP - 275.0) / 25.0, 2.0));
        double cBarP7 = Math.Pow(cBarP, 7.0);
        double rc = 2.0 * Math.Sqrt(cBarP7 / (cBarP7 + pow25To7));
        double lMinus50Sq = (lBarP - 50.0) * (lBarP - 50.0);
        double sl = 1.0 + 0.015 * lMinus50Sq / Math.Sqrt(20.0 + lMinus50Sq);
        double sc = 1.0 + 0.045 * cBarP;
        double sh = 1.0 + 0.015 * cBarP * t;
        double rt = -Math.Sin(2.0 * dTheta * deg) * rc;

        double lTerm = dLp / sl, cTerm = dCp / sc, hTerm = dHp / sh;
        return Math.Sqrt(lTerm * lTerm + cTerm * cTerm + hTerm * hTerm + rt * cTerm * hTerm);
    }

    private static double SrgbToLinear(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double LabF(double t)
    {
        const double epsilon = 216.0 / 24389.0;
        const double kappa = 24389.0 / 27.0;
        return t > epsilon ? Math.Pow(t, 1.0 / 3.0) : (kappa * t + 16.0) / 116.0;
    }

    private static double HueDegrees(double b, double aPrime)
    {
        if (b == 0.0 && aPrime == 0.0)
            return 0.0;
        double h = Math.Atan2(b, aPrime) * 180.0 / Math.PI;
        return h < 0.0 ? h + 360.0 : h;
    }
}
