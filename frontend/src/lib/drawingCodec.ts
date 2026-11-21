export type NormalizedPoint = [number, number]

export interface DrawingStroke {
  pts: NormalizedPoint[]
  size: number
  eraser?: boolean
}

export interface CompressedDrawingImage {
  /** Local preview data URL (uncompressed data:image/...;base64,...) */
  dataUrl: string
  /** GZIP-compressed image bytes encoded as raw base64 (no data: prefix) */
  base64: string
  mime: 'image/png' | 'image/jpeg'
  compression: 'gzip'
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

async function gzipCompressBytes(input: Uint8Array): Promise<Uint8Array> {
  if (typeof CompressionStream === 'undefined') {
    return input
  }
  try {
    const compressedStream = new Blob([new Uint8Array(input)])
      .stream()
      .pipeThrough(new CompressionStream('gzip'))
    const buffer = await new Response(compressedStream).arrayBuffer()
    return new Uint8Array(buffer)
  } catch {
    return input
  }
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
 * back into a playable `data:image/...;base64,...` data URL.
 */
export async function decompressDrawingImageToDataUrl(
  base64Payload: string,
  mime: 'image/png' | 'image/jpeg' = 'image/png'
): Promise<string> {
  if (!base64Payload) return ''
  const compressedBytes = dataUrlToBytes(base64Payload)
  const rawImageBytes = await gzipDecompressBytes(compressedBytes)
  return `data:${mime};base64,${bytesToBase64(rawImageBytes)}`
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
 * Always renders the strokes in white (#ffffff) on black (#000000) at <= 512x512,
 * GZIP-compresses the binary image bytes, and returns a base64 payload guaranteed
 * to fit within the relay's 64 KB WebSocket frame limit.
 */
export async function compressWhiteDrawingImage(params: {
  cssWidth: number
  cssHeight: number
  strokes: DrawingStroke[]
}): Promise<CompressedDrawingImage> {
  const { cssWidth, cssHeight, strokes } = params
  const candidateSizes = [MAX_CANVAS_PX, 384, 256] as const
  const jpegQualities = [0.8, 0.65, 0.5] as const

  const offscreen = document.createElement('canvas')
  const ctx = offscreen.getContext('2d')

  for (const maxDim of candidateSizes) {
    const scale = Math.min(1, maxDim / Math.max(cssWidth, cssHeight))
    const pixelWidth = Math.min(
      maxDim,
      Math.max(1, Math.round(cssWidth * scale))
    )
    const pixelHeight = Math.min(
      maxDim,
      Math.max(1, Math.round(cssHeight * scale))
    )

    offscreen.width = pixelWidth
    offscreen.height = pixelHeight

    if (!ctx) {
      return {
        dataUrl: '',
        base64: '',
        mime: 'image/png',
        compression: 'gzip',
        width: pixelWidth,
        height: pixelHeight,
        compressedBase64Bytes: 0,
      }
    }

    ctx.setTransform(
      pixelWidth / cssWidth,
      0,
      0,
      pixelHeight / cssHeight,
      0,
      0
    )
    renderStrokesToContext(ctx, cssWidth, cssHeight, strokes, '#ffffff')

    // 1. Encode PNG and always GZIP-compress its binary bytes
    const pngDataUrl = offscreen.toDataURL('image/png')
    const pngGzipped = await gzipCompressBytes(dataUrlToBytes(pngDataUrl))
    const pngBase64 = bytesToBase64(pngGzipped)
    if (pngBase64.length <= MAX_IMAGE_BASE64_BYTES) {
      return {
        dataUrl: pngDataUrl,
        base64: pngBase64,
        mime: 'image/png',
        compression: 'gzip',
        width: pixelWidth,
        height: pixelHeight,
        compressedBase64Bytes: pngBase64.length,
      }
    }

    // 2. If still above budget, encode JPEG and GZIP-compress its binary bytes
    for (const quality of jpegQualities) {
      const jpgDataUrl = offscreen.toDataURL('image/jpeg', quality)
      const jpgGzipped = await gzipCompressBytes(dataUrlToBytes(jpgDataUrl))
      const jpgBase64 = bytesToBase64(jpgGzipped)
      if (jpgBase64.length <= MAX_IMAGE_BASE64_BYTES) {
        return {
          dataUrl: jpgDataUrl,
          base64: jpgBase64,
          mime: 'image/jpeg',
          compression: 'gzip',
          width: pixelWidth,
          height: pixelHeight,
          compressedBase64Bytes: jpgBase64.length,
        }
      }
    }
  }

  // Final fallback: 256x256 JPEG q=0.4 + GZIP
  const fallbackDataUrl = offscreen.toDataURL('image/jpeg', 0.4)
  const fallbackGzipped = await gzipCompressBytes(
    dataUrlToBytes(fallbackDataUrl)
  )
  const fallbackBase64 = bytesToBase64(fallbackGzipped)
  return {
    dataUrl: fallbackDataUrl,
    base64: fallbackBase64,
    mime: 'image/jpeg',
    compression: 'gzip',
    width: offscreen.width,
    height: offscreen.height,
    compressedBase64Bytes: fallbackBase64.length,
  }
}
