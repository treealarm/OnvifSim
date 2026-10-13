import type { ThreeEvent } from '@react-three/fiber';
import { useEffect, useMemo } from 'react';
import { CanvasTexture, SRGBColorSpace } from 'three';

// Share of the viewport height a label takes, whatever its distance.
const labelHeight = 0.022;
const fontPx = 28;

// A name tag drawn over the scene at a point, the same size however far away it is.
export function Label({ text, position, onClick }: { text: string; position: [number, number, number]; onClick?: (e: ThreeEvent<MouseEvent>) => void }) {
  const texture = useMemo(() => labelTexture(text), [text]);
  useEffect(() => () => texture.dispose(), [texture]);
  const aspect = texture.image.width / texture.image.height;
  return (
    <sprite position={position} center={[0.5, 0]} scale={[labelHeight * aspect, labelHeight, 1]} renderOrder={10} onClick={onClick}>
      <spriteMaterial map={texture} sizeAttenuation={false} depthTest={false} transparent />
    </sprite>
  );
}

function labelTexture(text: string): CanvasTexture {
  const canvas = document.createElement('canvas');
  const context = canvas.getContext('2d');
  if (!context) throw new Error('No 2D canvas for labels.');
  const font = `${fontPx}px system-ui, sans-serif`;
  context.font = font;
  canvas.width = Math.ceil(context.measureText(text).width) + fontPx;
  canvas.height = Math.round(fontPx * 1.5);
  // Resizing the canvas reset the context.
  context.font = font;
  context.fillStyle = 'rgba(0, 0, 0, 0.55)';
  context.beginPath();
  context.roundRect(0, 0, canvas.width, canvas.height, fontPx / 4);
  context.fill();
  context.fillStyle = '#ffffff';
  context.textBaseline = 'middle';
  context.fillText(text, fontPx / 2, canvas.height / 2);
  const texture = new CanvasTexture(canvas);
  texture.colorSpace = SRGBColorSpace;
  return texture;
}
