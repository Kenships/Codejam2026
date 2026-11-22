using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Small standalone QR code encoder (ISO/IEC 18004, byte mode, versions 1-40).
/// Used for the lobby's join link, so phones can scan straight into the room.
///   Texture2D tex = QrCode.CreateTexture("https://example.com/join?room=ABCD");
/// </summary>
public static class QrCode
{
    public enum Ecc { Low = 0, Medium = 1, Quartile = 2, High = 3 }

    // Format-info bits for each level (L, M, Q, H)
    private static readonly int[] EccFormatBits = { 1, 0, 3, 2 };

    // [ecc][version], index 0 unused
    private static readonly int[][] EccCodewordsPerBlock =
    {
        new[] { -1,  7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    private static readonly int[][] NumEccBlocks =
    {
        new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4,  4,  4,  4,  4,  6,  6,  6,  6,  7,  8,  8,  9,  9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5,  5,  8,  9,  9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8,  8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    /// <summary>
    /// Encodes text (UTF-8) and returns the module grid as [row, column], true = dark.
    /// Uses the smallest version that fits, and raises the error correction level if
    /// that doesn't make the code bigger.
    /// </summary>
    public static bool[,] Encode(string text, Ecc minEcc = Ecc.Medium)
    {
        byte[] data = Encoding.UTF8.GetBytes(text ?? "");

        int version = 1;
        for (; ; version++)
        {
            if (version > 40)
                throw new ArgumentException("Text is too long for a QR code.", nameof(text));
            if (DataBitsNeeded(data.Length, version) <= DataCodewords(version, minEcc) * 8)
                break;
        }

        Ecc ecc = minEcc;
        for (Ecc e = Ecc.High; e > minEcc; e--)
        {
            if (DataBitsNeeded(data.Length, version) <= DataCodewords(version, e) * 8)
            {
                ecc = e;
                break;
            }
        }

        byte[] codewords = AddEccAndInterleave(BuildDataCodewords(data, version, ecc), version, ecc);
        return new Symbol(version).Build(codewords, ecc);
    }

    /// <summary>
    /// Renders a QR code to a texture: black on white, with the standard 4-module quiet zone.
    /// The caller owns the texture and should Destroy it.
    /// </summary>
    public static Texture2D CreateTexture(string text, int pixelsPerModule = 8, Ecc minEcc = Ecc.Medium)
    {
        const int quietZone = 4;
        bool[,] modules = Encode(text, minEcc);
        int size = modules.GetLength(0);
        int scale = Mathf.Max(1, pixelsPerModule);
        int pixels = (size + quietZone * 2) * scale;

        Color32 dark = new(0, 0, 0, 255);
        Color32 light = new(255, 255, 255, 255);
        var colors = new Color32[pixels * pixels];

        for (int py = 0; py < pixels; py++)
        {
            // Texture rows go bottom-up; QR rows go top-down
            int row = (pixels - 1 - py) / scale - quietZone;
            for (int px = 0; px < pixels; px++)
            {
                int col = px / scale - quietZone;
                bool isDark = row >= 0 && row < size && col >= 0 && col < size && modules[row, col];
                colors[py * pixels + px] = isDark ? dark : light;
            }
        }

        var texture = new Texture2D(pixels, pixels, TextureFormat.RGBA32, false)
        {
            name = "QR Code",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave,
        };
        texture.SetPixels32(colors);
        texture.Apply(false, true);
        return texture;
    }

    // Rounding, in modules: exposed corners of data modules, and the finder "eyes" (rounded more)
    private const float ModuleCornerRadius = 0.32f;
    private const float FinderOuterRadius = 2.1f; // 7x7 ring
    private const float FinderHoleRadius = 1.4f;  // 5x5 gap
    private const float FinderPupilRadius = 1.0f; // 3x3 centre

    /// <summary>
    /// Renders a QR code as a mask: white, rounded modules on a fully transparent background
    /// (quiet zone included), meant to be layered over a coloured plate and tinted to whichever
    /// ink reads on it (see <see cref="InkFor"/>); tinting multiplies, so white can become any colour.
    /// Exposed module corners are rounded, so touching modules stay joined into one shape, and the
    /// finder "eyes" are drawn as smooth rounded rings. The caller owns the texture and should Destroy it.
    /// </summary>
    public static Texture2D CreateMaskTexture(string text, int pixelsPerModule = 16, Ecc minEcc = Ecc.Medium)
    {
        const int quietZone = 4;
        bool[,] modules = Encode(text, minEcc);
        int size = modules.GetLength(0);
        int scale = Mathf.Max(1, pixelsPerModule);
        int pixels = (size + quietZone * 2) * scale;

        bool DarkAt(int x, int y) => x >= 0 && x < size && y >= 0 && y < size && modules[y, x];

        // Is the point (u, v), in module units from the symbol's top-left, inside a module's shape?
        bool InShape(float u, float v)
        {
            int col = Mathf.FloorToInt(u);
            int row = Mathf.FloorToInt(v);
            if (col < 0 || col >= size || row < 0 || row >= size)
                return false;

            // Finders are drawn as whole rounded rings rather than module by module
            if (FinderAt(col, row, size, out int fx, out int fy))
            {
                float lu = u - fx, lv = v - fy;
                return InRoundedRect(lu, lv, 2f, 2f, 5f, 5f, FinderPupilRadius)
                    || (InRoundedRect(lu, lv, 0f, 0f, 7f, 7f, FinderOuterRadius)
                        && !InRoundedRect(lu, lv, 1f, 1f, 6f, 6f, FinderHoleRadius));
            }

            if (!modules[row, col])
                return false;

            // Round a corner only where both neighbours beside it are empty
            float fu = u - col, fv = v - row;
            float r = ModuleCornerRadius;
            bool left = fu < r, right = fu > 1f - r, top = fv < r, bottom = fv > 1f - r;
            if ((left || right) && (top || bottom))
            {
                int dx = left ? -1 : 1, dy = top ? -1 : 1;
                if (!DarkAt(col + dx, row) && !DarkAt(col, row + dy))
                {
                    float du = fu - (left ? r : 1f - r), dv = fv - (top ? r : 1f - r);
                    return du * du + dv * dv <= r * r;
                }
            }
            return true;
        }

        // 2x2 supersampling for smooth rounded edges; colour stays white (even where transparent,
        // so mipmaps don't bleed dark fringes), only alpha carries the shape
        var colors = new Color32[pixels * pixels];
        for (int py = 0; py < pixels; py++)
        {
            // Texture rows go bottom-up; QR rows go top-down
            int top = pixels - 1 - py;
            for (int px = 0; px < pixels; px++)
            {
                int covered = 0;
                for (int s = 0; s < 4; s++)
                {
                    float u = (px + 0.25f + 0.5f * (s & 1)) / scale - quietZone;
                    float v = (top + 0.25f + 0.5f * (s >> 1)) / scale - quietZone;
                    if (InShape(u, v))
                        covered++;
                }
                colors[py * pixels + px] = new Color32(255, 255, 255, (byte)(covered * 255 / 4));
            }
        }

        // Rendered larger than it's shown and mipmapped, so the UI can scale it down to any size cleanly
        var texture = new Texture2D(pixels, pixels, TextureFormat.RGBA32, true)
        {
            name = "QR Code (Mask)",
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave,
        };
        texture.SetPixels32(colors, 0);
        texture.Apply(true, true);
        return texture;
    }

    /// <summary>
    /// Black or white, whichever contrasts more with <paramref name="background"/> (WCAG relative
    /// luminance): black on warm/bright hues like red, orange, yellow and green, white on deep ones
    /// like blue and violet. The worst case (a background around 18% luminance) still gets about
    /// 4.6:1, enough for phones to scan.
    /// </summary>
    public static Color InkFor(Color background)
    {
        Color lin = background.linear;
        float luminance = 0.2126f * lin.r + 0.7152f * lin.g + 0.0722f * lin.b;
        float contrastWithWhite = 1.05f / (luminance + 0.05f);
        float contrastWithBlack = (luminance + 0.05f) / 0.05f;
        return contrastWithBlack >= contrastWithWhite ? Color.black : Color.white;
    }

    /// <summary>True if (u, v) is inside the rectangle (x0, y0)-(x1, y1) with corners of radius r.</summary>
    private static bool InRoundedRect(float u, float v, float x0, float y0, float x1, float y1, float r)
    {
        if (u < x0 || u > x1 || v < y0 || v > y1)
            return false;
        float du = Mathf.Max(x0 + r - u, u - (x1 - r), 0f);
        float dv = Mathf.Max(y0 + r - v, v - (y1 - r), 0f);
        return du * du + dv * dv <= r * r;
    }

    /// <summary>True if (x, y) is in one of the three 7x7 finder patterns; outputs its top-left corner.</summary>
    private static bool FinderAt(int x, int y, int size, out int fx, out int fy)
    {
        fx = x < 7 ? 0 : x >= size - 7 ? size - 7 : -1;
        fy = y < 7 ? 0 : y >= size - 7 ? size - 7 : -1;
        return fx >= 0 && fy >= 0 && !(fx > 0 && fy > 0); // no finder bottom-right
    }

    #region Data

    private static int CharCountBits(int version) => version <= 9 ? 8 : 16;

    private static int DataBitsNeeded(int byteCount, int version) => 4 + CharCountBits(version) + byteCount * 8;

    private static int RawDataModules(int version)
    {
        int result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            int numAlign = version / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (version >= 7)
                result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int version, Ecc ecc) =>
        RawDataModules(version) / 8
        - EccCodewordsPerBlock[(int)ecc][version] * NumEccBlocks[(int)ecc][version];

    private static byte[] BuildDataCodewords(byte[] data, int version, Ecc ecc)
    {
        var bits = new List<bool>();
        void Append(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
                bits.Add(((value >> i) & 1) != 0);
        }

        Append(0b0100, 4); // byte mode
        Append(data.Length, CharCountBits(version));
        foreach (byte b in data)
            Append(b, 8);

        int capacityBits = DataCodewords(version, ecc) * 8;
        Append(0, Math.Min(4, capacityBits - bits.Count)); // terminator
        Append(0, (8 - bits.Count % 8) % 8);               // byte align
        for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11)
            Append(pad, 8);

        var result = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
                result[i >> 3] |= (byte)(0x80 >> (i & 7));
        }
        return result;
    }

    private static byte[] AddEccAndInterleave(byte[] data, int version, Ecc ecc)
    {
        int numBlocks = NumEccBlocks[(int)ecc][version];
        int blockEccLen = EccCodewordsPerBlock[(int)ecc][version];
        int rawCodewords = RawDataModules(version) / 8;
        int numShortBlocks = numBlocks - rawCodewords % numBlocks;
        int shortBlockLen = rawCodewords / numBlocks;

        byte[] divisor = ReedSolomonDivisor(blockEccLen);
        var blocks = new byte[numBlocks][];
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            int dataLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var block = new byte[shortBlockLen + 1];
            Array.Copy(data, k, block, 0, dataLen);
            k += dataLen;

            byte[] eccBytes = ReedSolomonRemainder(data, k - dataLen, dataLen, divisor);
            // Short blocks keep a gap at dataLen so every block's ECC lines up
            Array.Copy(eccBytes, 0, block, shortBlockLen + 1 - blockEccLen, blockEccLen);
            blocks[i] = block;
        }

        var result = new byte[rawCodewords];
        int n = 0;
        for (int i = 0; i < shortBlockLen + 1; i++)
        {
            for (int j = 0; j < numBlocks; j++)
            {
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                    result[n++] = blocks[j][i];
            }
        }
        return result;
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = (byte)GfMultiply(result[j], root);
                if (j + 1 < degree)
                    result[j] ^= result[j + 1];
            }
            root = GfMultiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, int offset, int length, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        for (int d = 0; d < length; d++)
        {
            int factor = data[offset + d] ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++)
                result[i] ^= (byte)GfMultiply(divisor[i], factor);
        }
        return result;
    }

    private static int GfMultiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    #endregion

    #region Symbol

    private sealed class Symbol
    {
        private readonly int _version;
        private readonly int _size;
        private readonly bool[,] _modules;   // [y, x]
        private readonly bool[,] _isFunction;

        public Symbol(int version)
        {
            _version = version;
            _size = version * 4 + 17;
            _modules = new bool[_size, _size];
            _isFunction = new bool[_size, _size];
        }

        public bool[,] Build(byte[] codewords, Ecc ecc)
        {
            DrawFunctionPatterns(ecc);
            DrawCodewords(codewords);

            int bestMask = 0;
            int bestPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(mask);
                DrawFormatBits(ecc, mask);
                int penalty = Penalty();
                if (penalty < bestPenalty)
                {
                    bestMask = mask;
                    bestPenalty = penalty;
                }
                ApplyMask(mask); // XOR again to undo
            }

            ApplyMask(bestMask);
            DrawFormatBits(ecc, bestMask);
            return _modules;
        }

        private void Set(int x, int y, bool dark)
        {
            _modules[y, x] = dark;
            _isFunction[y, x] = true;
        }

        private void DrawFunctionPatterns(Ecc ecc)
        {
            for (int i = 0; i < _size; i++)
            {
                Set(6, i, i % 2 == 0);
                Set(i, 6, i % 2 == 0);
            }

            DrawFinder(3, 3);
            DrawFinder(_size - 4, 3);
            DrawFinder(3, _size - 4);

            int[] align = AlignmentPositions();
            int last = align.Length - 1;
            for (int i = 0; i <= last; i++)
            {
                for (int j = 0; j <= last; j++)
                {
                    bool onFinder = (i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0);
                    if (!onFinder)
                        DrawAlignment(align[i], align[j]);
                }
            }

            DrawFormatBits(ecc, 0); // reserves the area; redrawn with the real mask later
            DrawVersion();
        }

        private void DrawFinder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || x >= _size || y < 0 || y >= _size)
                        continue;
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(x, y, dist != 2 && dist != 4);
                }
            }
        }

        private void DrawAlignment(int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                    Set(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }

        private int[] AlignmentPositions()
        {
            if (_version == 1)
                return Array.Empty<int>();

            int numAlign = _version / 7 + 2;
            int step = (_version * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
            var result = new int[numAlign];
            result[0] = 6;
            for (int i = numAlign - 1, pos = _size - 7; i >= 1; i--, pos -= step)
                result[i] = pos;
            return result;
        }

        private void DrawFormatBits(Ecc ecc, int mask)
        {
            int data = EccFormatBits[(int)ecc] << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++)
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;
            bool Bit(int i) => ((bits >> i) & 1) != 0;

            // Around the top-left finder
            for (int i = 0; i <= 5; i++)
                Set(8, i, Bit(i));
            Set(8, 7, Bit(6));
            Set(8, 8, Bit(7));
            Set(7, 8, Bit(8));
            for (int i = 9; i < 15; i++)
                Set(14 - i, 8, Bit(i));

            // Split between the other two finders
            for (int i = 0; i < 8; i++)
                Set(_size - 1 - i, 8, Bit(i));
            for (int i = 8; i < 15; i++)
                Set(8, _size - 15 + i, Bit(i));
            Set(8, _size - 8, true); // dark module
        }

        private void DrawVersion()
        {
            if (_version < 7)
                return;

            int rem = _version;
            for (int i = 0; i < 12; i++)
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = _version << 12 | rem;

            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = _size - 11 + i % 3;
                int b = i / 3;
                Set(a, b, bit);
                Set(b, a, bit);
            }
        }

        private void DrawCodewords(byte[] codewords)
        {
            int bit = 0;
            int totalBits = codewords.Length * 8;
            for (int right = _size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                    right = 5; // skip the vertical timing column

                bool upward = ((right + 1) & 2) == 0;
                for (int vert = 0; vert < _size; vert++)
                {
                    int y = upward ? _size - 1 - vert : vert;
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        if (_isFunction[y, x] || bit >= totalBits)
                            continue;
                        _modules[y, x] = ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
                        bit++;
                    }
                }
            }
        }

        private void ApplyMask(int mask)
        {
            for (int y = 0; y < _size; y++)
            {
                for (int x = 0; x < _size; x++)
                {
                    if (_isFunction[y, x])
                        continue;

                    bool invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => (x / 3 + y / 2) % 2 == 0,
                        5 => x * y % 2 + x * y % 3 == 0,
                        6 => (x * y % 2 + x * y % 3) % 2 == 0,
                        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    };
                    if (invert)
                        _modules[y, x] = !_modules[y, x];
                }
            }
        }

        private bool Dark(int x, int y, bool alongRow) => alongRow ? _modules[y, x] : _modules[x, y];

        /// <summary>Standard mask penalty (rules N1-N4); lower scans more reliably.</summary>
        private int Penalty()
        {
            int penalty = 0;

            // N1 (runs of 5+) and N3 (finder-like 1:1:3:1:1 with 4 light modules on a side),
            // first along rows, then along columns
            for (int pass = 0; pass < 2; pass++)
            {
                bool alongRow = pass == 0;
                for (int a = 0; a < _size; a++)
                {
                    int run = 1;
                    for (int b = 1; b <= _size; b++)
                    {
                        if (b < _size && Dark(b, a, alongRow) == Dark(b - 1, a, alongRow))
                        {
                            run++;
                            continue;
                        }
                        if (run >= 5)
                            penalty += 3 + (run - 5);
                        run = 1;
                    }

                    // Off-symbol modules count as light (they're the quiet zone)
                    bool At(int b) => b >= 0 && b < _size && Dark(b, a, alongRow);
                    for (int b = -4; b < _size; b++)
                    {
                        bool core = At(b) && !At(b + 1) && At(b + 2) && At(b + 3) && At(b + 4) && !At(b + 5) && At(b + 6);
                        if (!core)
                            continue;
                        bool lightBefore = !At(b - 1) && !At(b - 2) && !At(b - 3) && !At(b - 4);
                        bool lightAfter = !At(b + 7) && !At(b + 8) && !At(b + 9) && !At(b + 10);
                        if (lightBefore || lightAfter)
                            penalty += 40;
                    }
                }
            }

            // N2: 2x2 blocks of one colour
            for (int y = 0; y < _size - 1; y++)
            {
                for (int x = 0; x < _size - 1; x++)
                {
                    bool c = _modules[y, x];
                    if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1])
                        penalty += 3;
                }
            }

            // N4: how far the dark share is from 50%
            int dark = 0;
            foreach (bool m in _modules)
            {
                if (m)
                    dark++;
            }
            int total = _size * _size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            penalty += Math.Max(0, k) * 10;

            return penalty;
        }
    }

    #endregion
}
