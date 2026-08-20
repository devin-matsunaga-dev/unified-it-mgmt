import jsQR from 'jsqr'
import { useCallback, useEffect, useRef, useState } from 'react'

export type CameraStatus = 'idle' | 'starting' | 'scanning' | 'denied' | 'unavailable'

/** How long a code must be out of frame before it counts again. */
const repeatCooldownMs = 2500

/**
 * Drives the rear camera and reads a QR out of its frames. jsQR rather than the browser's
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
   * The last code read and when it was last still in frame. jsQR reads the same label on every frame
   * it is pointed at, so a continuous count would post the same asset sixty times a second. Holding
   * a label in view keeps refreshing `at`, which means a code only counts again once it has left the
   * frame for the cooldown — pointing at the next shelf works, lingering on one box does not.
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
        const found = jsQR(frame.data, frame.width, frame.height, { inversionAttempts: 'dontInvert' })
        if (found?.data) {
          const now = Date.now()
          const seenRecently = lastRead.current?.code === found.data
            && now - lastRead.current.at < repeatCooldownMs
          lastRead.current = { code: found.data, at: now }
          if (!seenRecently) {
            onCodeRef.current(found.data)
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
