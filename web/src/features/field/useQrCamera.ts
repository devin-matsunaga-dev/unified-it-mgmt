import { BarcodeFormat, DecodeHintType, MultiFormatReader, RGBLuminanceSource, BinaryBitmap, HybridBinarizer } from '@zxing/library'
import { useCallback, useEffect, useRef, useState } from 'react'

export type CameraStatus = 'idle' | 'starting' | 'scanning' | 'denied' | 'unavailable'

/** How long a code must be out of frame before it counts again. */
const repeatCooldownMs = 2500

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
export function useQrCamera(
  onCode: (code: string) => void,
  { continuous = false }: { continuous?: boolean } = {},
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

  const stop = useCallback(() => {
    cancelAnimationFrame(frameRef.current)
    streamRef.current?.getTracks().forEach((track) => track.stop())
    streamRef.current = null
    lastRead.current = null
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
        video: { facingMode: { ideal: 'environment' } },
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
      const readFrame = () => {
        if (!streamRef.current || !context || video.readyState !== video.HAVE_ENOUGH_DATA) {
          frameRef.current = requestAnimationFrame(readFrame)
          return
        }
        canvas.width = video.videoWidth
        canvas.height = video.videoHeight
        context.drawImage(video, 0, 0, canvas.width, canvas.height)
        const frame = context.getImageData(0, 0, canvas.width, canvas.height)
        const found = decode(reader, frame)
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
  }, [continuous])

  // The camera must not outlive the screen that opened it — a live rear camera behind a page the
  // technician has walked away from is both a battery drain and a light left on.
  useEffect(() => stop, [stop])

  return { videoRef, status, start, stop }
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
