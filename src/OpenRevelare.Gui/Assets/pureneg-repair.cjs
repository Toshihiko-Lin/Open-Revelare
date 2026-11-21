'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const [root, input, selection, output, w, h] = process.argv.slice(1);
const engine = path.join(root, 'vendor/film-dust/engine');
const { heal } = require(path.join(engine, 'heal.js'));
const { healLarge, CELL } = require(path.join(engine, 'bigheal.js'));

async function main() {
    const width = Number(w), height = Number(h), channels = 3;
    const [rgb, selected] = await Promise.all([fs.readFile(input), fs.readFile(selection)]);
    const image = new Uint16Array(rgb.buffer, rgb.byteOffset, rgb.length / 2);
    const pixels = new Uint32Array(selected.buffer, selected.byteOffset, selected.length / 4);
    const mask = new Uint8Array(width * height);
    for (const pixel of pixels) mask[pixel] = 1;

    if (width * height <= CELL * CELL) {
        await heal(image, width, height, channels, mask, null);
    } else {
        await healLarge(mask, null, width, height, 1, width, height, channels, {
            async read(x0, y0, x1, y1) {
                const stride = (x1 - x0) * channels;
                const tile = new Uint16Array(stride * (y1 - y0));
                for (let y = y0; y < y1; y++) {
                    const start = (y * width + x0) * channels;
                    tile.set(image.subarray(start, start + stride), (y - y0) * stride);
                }
                return tile;
            },
            async write(x0, y0, x1, y1, tile) {
                const stride = (x1 - x0) * channels;
                for (let y = y0; y < y1; y++) {
                    const start = (y - y0) * stride;
                    image.set(tile.subarray(start, start + stride), (y * width + x0) * channels);
                }
            },
        });
    }

    const result = new Uint16Array(pixels.length * channels);
    for (let i = 0; i < pixels.length; i++) {
        const offset = pixels[i] * channels;
        result.set(image.subarray(offset, offset + channels), i * channels);
    }
    await fs.writeFile(output, Buffer.from(result.buffer));
    process.stdout.write(JSON.stringify({ type: 'result' }) + '\n');
}

main().catch(error => {
    process.stderr.write(error.message + '\n');
    process.exitCode = 1;
});
