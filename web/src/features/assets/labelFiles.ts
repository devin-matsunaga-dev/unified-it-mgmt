import type { DownloadedFile } from '../../api/client'

/**
 * Both ways out of a generated label PDF. The file arrives as a blob because the download is an
 * authorized API call, so it cannot simply be an href the browser follows.
 */
export function saveFile({ blob, fileName }: DownloadedFile) {
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  document.body.append(anchor)
  anchor.click()
  anchor.remove()
  // Revoked on the next tick: Safari cancels a download whose object URL is released synchronously.
  window.setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

/**
 * Opens the PDF in the browser's own viewer, which is where the print dialog lives. The tab keeps the
 * object URL alive, so it is never revoked here.
 */
export function openFile({ blob }: DownloadedFile) {
  const opened = window.open(URL.createObjectURL(blob), '_blank', 'noopener')
  return opened !== null
}
