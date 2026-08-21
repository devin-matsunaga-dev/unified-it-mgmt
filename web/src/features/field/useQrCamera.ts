import { BarcodeFormat, DecodeHintType, MultiFormatReader, RGBLuminanceSource, BinaryBitmap, HybridBinarizer } from '@zxing/library'
import { useCallback, useEffect, useRef, useState } from 'react'

export type CameraStatus = 'idle' | 'starting' | 'scanning' | 'denied' | 'unavailable'

/** How long a code must be out of frame before it counts again. */
const repeatCooldownMs = 2500

/**
 * How long a shutter press keeps looking. Long enough to ride out the shake of the press itself,
 * short enough that a press which found nothing still feels like it answered.
 */
const captureWindowMs = 700

/**
 * What a technician can point this at. QR because that is what our own printed labels carry, and the
 * 1D families because that is what a manufacturer puts on a new device — Code 39 for a Dell service
 * tag, Code 128 for most HP, Lenovo and Apple serials. Listed explicitly rather than left to try
 * everything: every extra format is work done on every frame, and the ones omitted here (postal
 * codes, PDF417, Aztec) do not appear on IT hardware.
 */
const formats = [
  BarcodeFormat.QR_CODE,
  BarcodeFormat.DATA_MATRIX,
  BarcodeFormat.CODE_128,
  BarcodeFormat.CODE_39,
  BarcodeFormat.ITF,
  BarcodeFormat.EAN_13,
  BarcodeFormat.UPC_A,
]

export function createReader() {
  const reader = new MultiFormatReader()
  reader.setHints(new Map<DecodeHintType, unknown>([
    [DecodeHintType.POSSIBLE_FORMATS, formats],
    // Spend more effort per frame. A 1D barcode read off a curved sticker under bad light is a much
    // harder problem than a QR, and a missed frame here costs nothing but the next frame.
    [DecodeHintType.TRY_HARDER, true],
  ]))
  return reader
}

/**
 * Drives the rear camera and reads a barcode out of its frames. ZXing rather than the browser's
 * BarcodeDetector because that API is not carried by every phone this has to run on, and a scanner
 * that works on some handsets is worse than one behaviour everywhere.
 *
 * The decode runs on a canvas at the video's own resolution, once per animation frame. By default it
 * stops the moment it reads something — a technician pointing at one label wants one answer. In
 * `continuous` mode it keeps going for a stock count, where the whole job is scanning label after
 * label without touching the phone between them.
 */
/**
 * The slice of the frame that is actually decoded, as a fraction of each side. Anything outside it is
 * ignored, which is what makes the on-screen guide mean something: device labels put the serial, the
 * product code and a shipping reference within a couple of centimetres of each other, and a decoder
 * reading the whole frame returns whichever it happened to resolve first.
 *
 * Defaults to a wide, short band because that is the shape of a 1D barcode and the shape of the guide
 * drawn over it. A QR screen passes a square.
 */
export type ScanRegion = { widthRatio: number; heightRatio: number }

export function useQrCamera(
  onCode: (code: string) => void,
  { continuous = false, region, manual = false }: {
    continuous?: boolean
    region?: ScanRegion
    /**
     * Decode only when <c>capture()</c> is called, rather than on every frame.
     *
     * A continuous reader takes whichever code resolves first, and on a device label the serial, the
     * product code and a shipping reference sit within a couple of centimetres — so cropping to the
     * guide narrowed the target but could not give the technician time to aim at it. A shutter does:
     * the preview runs, they line the barcode up, and the read happens when they say so.
     */
    manual?: boolean
  } = {},
) {
  const videoRef = useRef<HTMLVideoElement>(null)
  const [status, setStatus] = useState<CameraStatus>('idle')
  const streamRef = useRef<MediaStream | null>(null)
  const frameRef = useRef<number>(0)
  // Held in a ref so the scan loop always calls the latest handler without being torn down and
  // restarted — restarting it would drop the camera between frames.
  const onCodeRef = useRef(onCode)
  onCodeRef.current = onCode
  /**
   * The last code read and when it was last still in frame. The decoder reads the same label on every
   * frame it is pointed at, so a continuous count would post the same asset sixty times a second.
   * Holding a label in view keeps refreshing `at`, which means a code only counts again once it has
   * left the frame for the cooldown — pointing at the next shelf works, lingering on one box does not.
   */
  const lastRead = useRef<{ code: string; at: number } | null>(null)
  /** Reads the current frame once. Set when the camera opens, cleared when it stops. */
  const readOneFrame = useRef<(() => string | null) | null>(null)
  const [capturing, setCapturing] = useState(false)

  const stop = useCallback(() => {
    cancelAnimationFrame(frameRef.current)
    streamRef.current?.getTracks().forEach((track) => track.stop())
    streamRef.current = null
    lastRead.current = null
    readOneFrame.current = null
    setCapturing(false)
    setStatus('idle')
  }, [])

  const start = useCallback(async () => {
    if (!navigator.mediaDevices?.getUserMedia) {
      setStatus('unavailable')
      return
    }
    setStatus('starting')
    try {
      // `environment` is the rear camera. Ideal rather than exact so a laptop with only a front
      // camera still opens something rather than throwing.
      const stream = await navigator.mediaDevices.getUserMedia({
        // Ask for a lot of pixels. Left to itself a browser commonly hands back 640×480, and after
        // cropping to the guide that leaves a few hundred pixels of barcode or digits — which is why
        // reading anything meant holding the phone almost against the label. `ideal` rather than
        // `exact` so a camera that cannot manage it still opens at whatever it has.
        video: {
          facingMode: { ideal: 'environment' },
          width: { ideal: 1920 },
          height: { ideal: 1080 },
        },
        audio: false,
      })
      streamRef.current = stream
      const video = videoRef.current
      if (!video) {
        stream.getTracks().forEach((track) => track.stop())
        setStatus('idle')
        return
      }
      video.srcObject = stream
      // Required by iOS Safari, which otherwise takes the video full-screen and hides the page.
      video.setAttribute('playsinline', 'true')
      await video.play()
      setStatus('scanning')

      const reader = createReader()
      const canvas = document.createElement('canvas')
      const context = canvas.getContext('2d', { willReadFrequently: true })
      readOneFrame.current = () => {
        if (!context || video.readyState !== video.HAVE_ENOUGH_DATA) return null
        return decodeRegion(reader, context, canvas, video, region)
      }
      if (manual) {
        // No loop. The frames still stream to the <video> for the technician to aim with; nothing
        // reads them until capture() asks.
        return
      }
      const readFrame = () => {
        if (!streamRef.current || !context || video.readyState !== video.HAVE_ENOUGH_DATA) {
          frameRef.current = requestAnimationFrame(readFrame)
          return
        }
        const found = decodeRegion(reader, context, canvas, video, region)
        if (found) {
          const now = Date.now()
          const seenRecently = lastRead.current?.code === found && now - lastRead.current.at < repeatCooldownMs
          lastRead.current = { code: found, at: now }
          if (!seenRecently) {
            onCodeRef.current(found)
            if (!continuous) return
          }
        }
        frameRef.current = requestAnimationFrame(readFrame)
      }
      frameRef.current = requestAnimationFrame(readFrame)
    } catch (error) {
      // A refused permission and a camera that is not there are different problems for the person
      // holding the phone: one is fixed in Settings, the other means typing the tag instead.
      setStatus((error as Error).name === 'NotAllowedError' ? 'denied' : 'unavailable')
    }
  }, [continuous, region?.widthRatio, region?.heightRatio])

  /**
   * Reads what is in the guide right now, for a shutter. Samples for a short window rather than
   * taking exactly one frame: the instant a finger presses the screen is often the blurriest moment
   * there is, and a single-frame shutter would make the technician press twice for a barcode that
   * was correctly aimed the whole time.
   *
   * Resolves false when nothing read, so the caller can say so rather than leaving a press that
   * appears to do nothing.
   */
  const capture = useCallback(async () => {
    if (!readOneFrame.current) return false
    setCapturing(true)
    try {
      const deadline = Date.now() + captureWindowMs
      while (Date.now() < deadline) {
        const found = readOneFrame.current?.()
        if (found) {
          onCodeRef.current(found)
          return true
        }
        await new Promise((resolve) => requestAnimationFrame(() => resolve(null)))
      }
      return false
    } finally {
      setCapturing(false)
    }
  }, [])

  // The camera must not outlive the screen that opened it — a live rear camera behind a page the
  // technician has walked away from is both a battery drain and a light left on.
  useEffect(() => stop, [stop])

  return { videoRef, status, start, stop, capture, capturing }
}

/**
 * One frame, or null. ZXing throws NotFoundException for a frame carrying no code, which is the
 * normal case on almost every frame — so this is a miss, never an error worth surfacing.
 */
export function decode(reader: MultiFormatReader, frame: ImageData): string | null {
  try {
    const source = new RGBLuminanceSource(
      convertToLuminance(frame), frame.width, frame.height)
    // decodeWithState, never decode: `decode(image)` with no hints argument calls setHints(undefined)
    // internally, which throws away the format list above and rebuilds the full reader set — every
    // symbology ZXing knows, tried on every video frame. The library documents this call as the one
    // for continuous scanning, and the difference is visible as MaxiCode errors in the console.
    const result = reader.decodeWithState(new BinaryBitmap(new HybridBinarizer(source)))
    return result.getText() || null
  } catch {
    return null
  } finally {
    reader.reset()
  }
}

/**
 * RGBA frame to the 8-bit grey ZXing expects. Done here rather than by the library's own helper so
 * the loop allocates one array per frame instead of several.
 */
function convertToLuminance(frame: ImageData): Uint8ClampedArray {
  const { data, width, height } = frame
  const grey = new Uint8ClampedArray(width * height)
  for (let index = 0, pixel = 0; index < data.length; index += 4, pixel += 1) {
    // Rec. 601 luma, the weighting ZXing's own converter uses.
    grey[pixel] = (data[index] * 0.299 + data[index + 1] * 0.587 + data[index + 2] * 0.114) | 0
  }
  return grey
}

/**
 * Draws the guide's area onto the canvas and decodes only that. Cropping at capture rather than
 * filtering results afterwards is what makes aiming work at all — a decoder given the whole frame
 * has already chosen before anything downstream could reject its choice.
 */
function decodeRegion(
  reader: MultiFormatReader,
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  video: HTMLVideoElement,
  region: ScanRegion | undefined,
): string | null {
  return decode(reader, cropRegion(context, canvas, video, region))
}

/** The crop both paths share, so a decode and a recognition see the same pixels. */
function cropRegion(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  video: HTMLVideoElement,
  region: ScanRegion | undefined,
): ImageData {
  const widthRatio = region?.widthRatio ?? 1
  const heightRatio = region?.heightRatio ?? 1
  const sourceWidth = Math.max(1, Math.round(video.videoWidth * widthRatio))
  const sourceHeight = Math.max(1, Math.round(video.videoHeight * heightRatio))
  const sourceX = Math.round((video.videoWidth - sourceWidth) / 2)
  const sourceY = Math.round((video.videoHeight - sourceHeight) / 2)
  canvas.width = sourceWidth
  canvas.height = sourceHeight
  context.drawImage(video, sourceX, sourceY, sourceWidth, sourceHeight, 0, 0, sourceWidth, sourceHeight)
  return context.getImageData(0, 0, sourceWidth, sourceHeight)
}
