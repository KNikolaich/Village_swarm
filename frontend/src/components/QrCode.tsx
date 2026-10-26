import { useEffect, useState } from 'react'

/** A QR code of the text, drawn as an SVG on white (the camera of a phone needs the light background in any theme). */
export function QrCode({ text, label, size = 200 }: { text: string; label: string; size?: number }) {
  const [svg, setSvg] = useState('')
  useEffect(() => {
    let current = true
    // the encoder (~25 KB) is only loaded where a code is shown
    void import('qrcode')
      .then((qr) => qr.toString(text, { type: 'svg', margin: 1, errorCorrectionLevel: 'M' }))
      .then((s) => current && setSvg(s))
      .catch(() => current && setSvg(''))
    return () => { current = false }
  }, [text])
  return (
    <div
      role="img"
      aria-label={label}
      className="shrink-0 rounded bg-white p-1 [&>svg]:size-full"
      style={{ width: size, height: size }}
      dangerouslySetInnerHTML={{ __html: svg }}
    />
  )
}
