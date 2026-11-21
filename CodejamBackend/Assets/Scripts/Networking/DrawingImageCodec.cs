using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace Codejam.Networking
{
    [Serializable]
    public class DrawingSubmitPayload
    {
        public string t;
        public string prompt;
        public string image;
        public string mime;
        public string compression;
        public int w;
        public int h;
    }

    /// <summary>
    /// Decompresses the GZIP + Base64 encoded white-on-black drawing payload sent by the React frontend
    /// (d.t == "drawing_submit") into a Unity Texture2D or Sprite.
    /// </summary>
    public static class DrawingImageCodec
    {
        // GZIP magic header bytes: 0x1F, 0x8B
        private const byte GzipMagic0 = 0x1F;
        private const byte GzipMagic1 = 0x8B;

        /// <summary>
        /// Always decodes Base64 and GZIP-decompresses the incoming image bytes.
        /// Safely handles raw image bytes if the payload was not GZIP-wrapped.
        /// </summary>
        public static byte[] DecompressImageBytes(string base64Payload)
        {
            if (string.IsNullOrWhiteSpace(base64Payload))
            {
                return Array.Empty<byte>();
            }

            // Strip any accidental "data:image/...;base64," prefix if present
            int commaIndex = base64Payload.IndexOf(',');
            string rawBase64 = commaIndex >= 0
                ? base64Payload.Substring(commaIndex + 1)
                : base64Payload;

            byte[] compressedBytes = Convert.FromBase64String(rawBase64);
            if (compressedBytes.Length < 2 ||
                compressedBytes[0] != GzipMagic0 ||
                compressedBytes[1] != GzipMagic1)
            {
                return compressedBytes;
            }

            using var inputStream = new MemoryStream(compressedBytes);
            using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream();
            gzipStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }

        /// <summary>
        /// Decompresses a GZIP+Base64 image payload from the frontend into a Unity Texture2D.
        /// Caller owns the returned Texture2D and should UnityEngine.Object.Destroy() it when done.
        /// </summary>
        public static Texture2D DecompressToTexture2D(
            string base64Payload,
            FilterMode filterMode = FilterMode.Bilinear)
        {
            byte[] imageBytes = DecompressImageBytes(base64Payload);
            if (imageBytes.Length == 0)
            {
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = filterMode,
                wrapMode = TextureWrapMode.Clamp
            };

            if (!ImageConversion.LoadImage(texture, imageBytes, markNonReadable: false))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            return texture;
        }

        /// <summary>
        /// Decompresses a DrawingSubmitPayload into a ready-to-render Unity Sprite.
        /// </summary>
        public static Sprite DecompressToSprite(
            DrawingSubmitPayload payload,
            float pixelsPerUnit = 100f)
        {
            if (payload == null)
            {
                return null;
            }

            Texture2D tex = DecompressToTexture2D(payload.image);
            if (tex == null)
            {
                return null;
            }

            return Sprite.Create(
                tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit
            );
        }
    }
}
