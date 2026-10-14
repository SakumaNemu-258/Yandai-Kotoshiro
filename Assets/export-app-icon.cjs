// Optional artwork export: node Assets/export-app-icon.cjs (requires sharp).
// The application build consumes the checked-in ICO directly.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require('sharp');

async function exportIcon() {
  const sizes = [16, 24, 32, 48, 64, 128, 256];
  const frames = [];
  for (const size of sizes) {
    const artwork = fs.readFileSync(path.join(__dirname,
      size === 16 ? 'AppIcon.Small.Light.svg' : 'AppIcon.Light.svg'));
    const png = await sharp(artwork, { density: 288 }).resize(size, size).png().toBuffer();
    const { data, info } = await sharp(png).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    if (info.width !== size || info.height !== size || info.channels !== 4 || data[3] !== 0)
      throw new Error(`Invalid transparent icon frame: ${size}`);
    const center = (Math.floor(size / 2) * size + Math.floor(size / 2)) * 4;
    if (!data.subarray(center, center + 4).equals(Buffer.from([246, 246, 248, 255])))
      throw new Error(`Icon frame is not the white variant: ${size}`);
    frames.push({ size, png });
  }
  const directory = Buffer.alloc(6 + frames.length * 16);
  directory.writeUInt16LE(1, 2);
  directory.writeUInt16LE(frames.length, 4);
  let offset = directory.length;
  frames.forEach(({ size, png }, index) => {
    const entry = 6 + index * 16;
    directory[entry] = directory[entry + 1] = size === 256 ? 0 : size;
    directory.writeUInt16LE(1, entry + 4);
    directory.writeUInt16LE(32, entry + 6);
    directory.writeUInt32LE(png.length, entry + 8);
    directory.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  });
  const icon = Buffer.concat([directory, ...frames.map(frame => frame.png)]);
  fs.writeFileSync(path.join(__dirname, 'AppIcon.ico'), icon);
  console.log(`White application ICO: ${sizes.join('/')}px, transparent corners.`);
  console.log(`SHA256: ${crypto.createHash('sha256').update(icon).digest('hex').toUpperCase()}`);
}

exportIcon().catch(error => { console.error(error.message); process.exitCode = 1; });
