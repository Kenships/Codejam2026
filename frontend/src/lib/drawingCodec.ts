export type NormalizedPoint = [number, number]

export interface DrawingStroke {
  pts: NormalizedPoint[]
  size: number
  eraser?: boolean
}

/**
 * `image/x-truehue-bitmap` is our 1-bit format (see encodeBitmap). PNG/JPEG are still
 * decoded so profile pictures saved by an older build keep working.
 */
export type DrawingMime = 'image/png' | 'image/jpeg' | 'image/x-truehue-bitmap'
export const BITMAP_MIME = 'image/x-truehue-bitmap' as const

export function toDrawingMime(value: unknown): DrawingMime {
  return value === 'image/jpeg' || value === BITMAP_MIME ? value : 'image/png'
}

export interface CompressedDrawingImage {
  /** Local preview data URL (uncompressed data:image/...;base64,...) */
  dataUrl: string
  /** GZIP-compressed bitmap bytes encoded as raw base64 (no data: prefix) */
  base64: string
  mime: DrawingMime
  /** 'none' only when the browser has no CompressionStream; decoders sniff the gzip header anyway. */
  compression: 'gzip' | 'none'
  width: number
  height: number
  /** Size in bytes of the compressed base64 string */
  compressedBase64Bytes: number
}

export const MAX_CANVAS_PX = 512
/** Safe base64 budget so the full {"t":"send","to":"host","d":{...}} JSON frame stays well under 64 KB */
export const MAX_IMAGE_BASE64_BYTES = 45_000
export const CANVAS_BG = '#000000'

export function renderStrokesToContext(
  ctx: CanvasRenderingContext2D,
  width: number,
  height: number,
  strokes: DrawingStroke[],
  inkColor: string
): void {
  ctx.fillStyle = CANVAS_BG
  ctx.fillRect(0, 0, width, height)

  ctx.lineCap = 'round'
  ctx.lineJoin = 'round'

  for (const stroke of strokes) {
    if (stroke.pts.length === 0) continue
    ctx.strokeStyle = stroke.eraser ? CANVAS_BG : inkColor
    ctx.fillStyle = stroke.eraser ? CANVAS_BG : inkColor
    ctx.lineWidth = stroke.size

    if (stroke.pts.length === 1) {
      const [nx, ny] = stroke.pts[0]
      ctx.beginPath()
      ctx.arc(nx * width, ny * height, stroke.size / 2, 0, Math.PI * 2)
      ctx.fill()
      continue
    }

    ctx.beginPath()
    ctx.moveTo(stroke.pts[0][0] * width, stroke.pts[0][1] * height)
    for (let i = 1; i < stroke.pts.length; i += 1) {
      ctx.lineTo(stroke.pts[i][0] * width, stroke.pts[i][1] * height)
    }
    ctx.stroke()
  }
}

function dataUrlToBytes(dataUrl: string): Uint8Array {
  const commaIdx = dataUrl.indexOf(',')
  const base64 = commaIdx >= 0 ? dataUrl.slice(commaIdx + 1) : dataUrl
  const binary = atob(base64)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i)
  }
  return bytes
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = ''
  const chunkSize = 0x8000
  for (let i = 0; i < bytes.length; i += chunkSize) {
    const slice = bytes.subarray(i, i + chunkSize)
    binary += String.fromCharCode(...slice)
  }
  return btoa(binary)
}

async function gzipCompressBytes(
  input: Uint8Array
): Promise<{ bytes: Uint8Array; gzipped: boolean }> {
  if (typeof CompressionStream === 'undefined') {
    return { bytes: input, gzipped: false }
  }
  try {
    const compressedStream = new Blob([new Uint8Array(input)])
      .stream()
      .pipeThrough(new CompressionStream('gzip'))
    const buffer = await new Response(compressedStream).arrayBuffer()
    return { bytes: new Uint8Array(buffer), gzipped: true }
  } catch {
    return { bytes: input, gzipped: false }
  }
}

// ---- 1-bit bitmap format ----
//
// Drawings are only ever white ink on black, so each pixel is one bit.
//   bytes 0-2  magic "TH1"
//   bytes 3-4  width  (uint16, little-endian)
//   bytes 5-6  height (uint16, little-endian)
//   bytes 7..  ceil(w*h/8) bytes of pixels, row-major from the top-left,
//              most significant bit first; 1 = ink, 0 = background
// The whole thing is then gzipped. A 512x512 drawing is 32 KB raw (fits the
// frame budget even uncompressed) and usually a few KB gzipped.
const BITMAP_MAGIC = [0x54, 0x48, 0x31] as const // "TH1"
const BITMAP_HEADER_BYTES = 7
/** Pixels at least this bright count as ink (anti-aliased edges are split down the middle). */
const INK_THRESHOLD = 128

function isBitmap(bytes: Uint8Array): boolean {
  return (
    bytes.length >= BITMAP_HEADER_BYTES &&
    bytes[0] === BITMAP_MAGIC[0] &&
    bytes[1] === BITMAP_MAGIC[1] &&
    bytes[2] === BITMAP_MAGIC[2]
  )
}

function encodeBitmap(image: ImageData): Uint8Array {
  const { width, height, data } = image
  const out = new Uint8Array(BITMAP_HEADER_BYTES + Math.ceil((width * height) / 8))
  out.set(BITMAP_MAGIC, 0)
  out[3] = width & 0xff
  out[4] = width >> 8
  out[5] = height & 0xff
  out[6] = height >> 8

  const pixelCount = width * height
  for (let i = 0; i < pixelCount; i += 1) {
    // White on black: the red channel is the brightness
    if (data[i * 4] >= INK_THRESHOLD) {
      out[BITMAP_HEADER_BYTES + (i >> 3)] |= 0x80 >> (i & 7)
    }
  }
  return out
}

/** Renders a decoded bitmap as a white-on-black PNG data URL. */
function bitmapToDataUrl(bytes: Uint8Array): string {
  const width = bytes[3] | (bytes[4] << 8)
  const height = bytes[5] | (bytes[6] << 8)
  if (width === 0 || height === 0) return ''

  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const ctx = canvas.getContext('2d')
  if (!ctx) return ''

  const image = ctx.createImageData(width, height)
  const pixelCount = width * height
  for (let i = 0; i < pixelCount; i += 1) {
    const byte = bytes[BITMAP_HEADER_BYTES + (i >> 3)] ?? 0
    const value = byte & (0x80 >> (i & 7)) ? 255 : 0
    const p = i * 4
    image.data[p] = value
    image.data[p + 1] = value
    image.data[p + 2] = value
    image.data[p + 3] = 255
  }
  ctx.putImageData(image, 0, 0)
  return canvas.toDataURL('image/png')
}

async function gzipDecompressBytes(input: Uint8Array): Promise<Uint8Array> {
  // Check GZIP magic header (0x1f, 0x8b)
  const isGzip = input.length >= 2 && input[0] === 0x1f && input[1] === 0x8b
  if (!isGzip || typeof DecompressionStream === 'undefined') {
    return input
  }
  try {
    const decompressedStream = new Blob([new Uint8Array(input)])
      .stream()
      .pipeThrough(new DecompressionStream('gzip'))
    const buffer = await new Response(decompressedStream).arrayBuffer()
    return new Uint8Array(buffer)
  } catch {
    return input
  }
}

/**
 * Decompresses a GZIP+Base64 image payload received from the relay/backend
 * back into a playable `data:image/...;base64,...` data URL. The format is
 * sniffed from the bytes, so `mime` only matters for old PNG/JPEG payloads.
 */
export async function decompressDrawingImageToDataUrl(
  base64Payload: string,
  mime: DrawingMime = 'image/png'
): Promise<string> {
  if (!base64Payload) return ''
  const compressedBytes = dataUrlToBytes(base64Payload)
  const rawImageBytes = await gzipDecompressBytes(compressedBytes)
  if (isBitmap(rawImageBytes)) {
    return bitmapToDataUrl(rawImageBytes)
  }
  const legacyMime = mime === BITMAP_MIME ? 'image/png' : mime
  return `data:${legacyMime};base64,${bytesToBase64(rawImageBytes)}`
}

const MOCK_STROKES: DrawingStroke[] = [
  // Circle
  {
    size: 10,
    pts: Array.from({ length: 33 }, (_, i) => {
      const angle = (i / 32) * Math.PI * 2
      return [
        Math.round((0.5 + Math.cos(angle) * 0.22) * 1000) / 1000,
        Math.round((0.5 + Math.sin(angle) * 0.22) * 1000) / 1000,
      ] as NormalizedPoint
    }),
  },
  // Rays
  ...[0, 45, 90, 135, 180, 225, 270, 315].map((deg): DrawingStroke => {
    const rad = (deg * Math.PI) / 180
    return {
      size: 8,
      pts: [
        [
          Math.round((0.5 + Math.cos(rad) * 0.28) * 1000) / 1000,
          Math.round((0.5 + Math.sin(rad) * 0.28) * 1000) / 1000,
        ],
        [
          Math.round((0.5 + Math.cos(rad) * 0.4) * 1000) / 1000,
          Math.round((0.5 + Math.sin(rad) * 0.4) * 1000) / 1000,
        ],
      ],
    }
  }),
]

export async function createMockCompressedDrawing(): Promise<CompressedDrawingImage> {
  return compressWhiteDrawingImage({
    cssWidth: 320,
    cssHeight: 320,
    strokes: MOCK_STROKES,
  })
}

/**
 * Renders the strokes in white (#ffffff) on black (#000000) at <= 512x512, packs
 * them into the 1-bit bitmap format (see encodeBitmap) and GZIP-compresses it.
 * Even uncompressed, a 512x512 bitmap stays under MAX_IMAGE_BASE64_BYTES.
 */
export async function compressWhiteDrawingImage(params: {
  cssWidth: number
  cssHeight: number
  strokes: DrawingStroke[]
}): Promise<CompressedDrawingImage> {
  const { cssWidth, cssHeight, strokes } = params

  const scale = Math.min(1, MAX_CANVAS_PX / Math.max(cssWidth, cssHeight))
  const pixelWidth = Math.min(MAX_CANVAS_PX, Math.max(1, Math.round(cssWidth * scale)))
  const pixelHeight = Math.min(MAX_CANVAS_PX, Math.max(1, Math.round(cssHeight * scale)))

  const offscreen = document.createElement('canvas')
  offscreen.width = pixelWidth
  offscreen.height = pixelHeight
  const ctx = offscreen.getContext('2d')

  if (!ctx) {
    return {
      dataUrl: '',
      base64: '',
      mime: BITMAP_MIME,
      compression: 'none',
      width: pixelWidth,
      height: pixelHeight,
      compressedBase64Bytes: 0,
    }
  }

  ctx.setTransform(pixelWidth / cssWidth, 0, 0, pixelHeight / cssHeight, 0, 0)
  renderStrokesToContext(ctx, cssWidth, cssHeight, strokes, '#ffffff')

  const bitmap = encodeBitmap(ctx.getImageData(0, 0, pixelWidth, pixelHeight))
  const { bytes, gzipped } = await gzipCompressBytes(bitmap)
  const base64 = bytesToBase64(bytes)

  return {
    dataUrl: offscreen.toDataURL('image/png'),
    base64,
    mime: BITMAP_MIME,
    compression: gzipped ? 'gzip' : 'none',
    width: pixelWidth,
    height: pixelHeight,
    compressedBase64Bytes: base64.length,
  }
}
