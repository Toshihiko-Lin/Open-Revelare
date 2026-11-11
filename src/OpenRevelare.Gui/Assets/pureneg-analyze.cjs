'use strict';

/*
 * Candidate attribution adapted from PURE Neg src/review.cjs.
 *
 * MIT License
 * Copyright (c) 2026 Film Dust for PhotoCraft contributors
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

const fs = require('node:fs/promises');
const path = require('node:path');
const { createHash } = require('node:crypto');
const [root, input, output, polarity, sensitivity, categories, fibreMapInput, runtimeInfo] = process.argv.slice(1);
const { processScan, optionsChecked } = require(path.join(root, 'src/processor.cjs'));
const { decodeTiff } = require(path.join(root, 'src/tiff.cjs'));
const { connectedRegions } = require(path.join(root, 'src/review.cjs'));
const native = require(path.join(root, 'vendor/film-dust/engine/native'));
const emit = event => process.stdout.write(JSON.stringify(event) + '\n');
const progress = event => emit({ type: 'progress', ...event, native: native.available() });
const modelAbort = new AbortController();

// Adapted from PURE Neg src/review.cjs (MIT). Keep the same attribution rules
// so the candidate labels match the standalone review workflow.
function detectionKind(mark) {
    if (mark.verdict !== 'defect') return undefined;
    if (mark.kind === 'scratch') return 'scratches';
    if (mark.kind === 'patch') return 'patches';
    if (mark.kind === 'hair/fibre' || mark.fibre === 'patch' || mark.fibreDisc === true) return 'fibers';
    if (['dust', 'speck', 'soft'].includes(mark.kind)) return 'dust';
    return undefined;
}

function attributeRegions(regions, runs, marks, width, height) {
    if (!regions.length || !marks.length) return regions;
    const count = runs.readUInt32LE(8);
    const kinds = new Map();
    const row = index => runs.readUInt32LE(28 + 16 * index);
    for (const mark of marks) {
        const kind = detectionKind(mark);
        if (!kind || ![mark.x0, mark.y0, mark.x1, mark.y1].every(Number.isFinite)) continue;
        const x0 = Math.max(0, Math.floor(mark.x0)), y0 = Math.max(0, Math.floor(mark.y0));
        const x1 = Math.min(width, Math.floor(mark.x1) + 1), y1 = Math.min(height, Math.floor(mark.y1) + 1);
        if (x0 >= x1 || y0 >= y1) continue;
        let first = 0, end = count;
        while (first < end) {
            const middle = (first + end) >>> 1;
            if (row(middle) < y0) first = middle + 1;
            else end = middle;
        }
        const overlaps = new Map();
        let total = 0, best = 0, winner;
        for (let i = first; i < count && row(i) < y1; i++) {
            const at = 24 + 16 * i;
            const pixels = Math.min(x1, runs.readUInt32LE(at + 12)) - Math.max(x0, runs.readUInt32LE(at + 8));
            if (pixels <= 0) continue;
            const id = runs.readUInt32LE(at);
            const hits = (overlaps.get(id) ?? 0) + pixels;
            overlaps.set(id, hits);
            total += pixels;
            if (hits > best) { best = hits; winner = id; }
        }
        const minimum = Number.isFinite(mark.area) ? Math.max(1, Math.ceil(mark.area * 0.2)) : 1;
        if (best < minimum || best * 2 <= total) continue;
        if (!kinds.has(winner)) kinds.set(winner, new Set());
        kinds.get(winner).add(kind);
    }
    return regions.map(region => {
        const labels = kinds.get(region.id);
        return labels ? { ...region, kinds: ['dust', 'scratches', 'fibers', 'patches'].filter(k => labels.has(k)) }
            : region;
    });
}

async function main() {
    await native.ready();
    progress({ fraction: .01, message: '读取检测图像' });
    const began = performance.now(), timings = {};
    let fibreMapPath = fibreMapInput || null;
    // Same CoreML protocols as upstream processFile. Keep only the input, map and
    // run index: writing full cleaned/mask TIFFs here would immediately discard
    // them and add hundreds of MB of disk I/O to every full-resolution review.
    const models = (async () => {
        let eyes = [];
        if (process.platform === 'darwin') {
            const { runImageCodec } = require(path.join(root, 'src/image-codec.cjs'));
            fibreMapPath = path.join(path.dirname(output), 'fibre-map.u8');
            const eyesPath = path.join(path.dirname(output), 'eyes.json');
            const eyeTask = (async () => {
                try {
                    await runImageCodec(['eyes', input, eyesPath], modelAbort.signal);
                    const found = JSON.parse(await fs.readFile(eyesPath, 'utf8'));
                    eyes = Array.isArray(found) ? found.filter(e => [e?.x, e?.y, e?.r].every(Number.isFinite)) : [];
                } catch { eyes = []; }
                finally { await fs.rm(eyesPath, { force: true }); }
            })();
            await Promise.all([eyeTask, runImageCodec(['fibremap', input, fibreMapPath], modelAbort.signal)]);
        }
        const fibreMap = fibreMapPath ? await fs.readFile(fibreMapPath) : null;
        timings.models_ready_seconds = (performance.now() - began) / 1000;
        return { eyes, fibreMap };
    })();
    models.catch(() => {});
    let image, sourceSha256;
    {
        const bytes = await fs.readFile(input);
        sourceSha256 = createHash('sha256').update(bytes).digest('hex');
        image = decodeTiff(bytes);
    }
    timings.decode_seconds = (performance.now() - began) / 1000;
    const modelInputs = await models;
    if (modelInputs.fibreMap && modelInputs.fibreMap.length !== image.width * image.height)
        throw new Error('FibreNet changed the source grid.');
    const options = {
        polarity, sensitivity: Number(sensitivity), detect: categories,
        ...modelInputs,
        // Use upstream's bounded working grid above 64MP. It retains both passes,
        // full-resolution fibre/scratch refinement and full-resolution mask output.
        conserveMemory: true,
        onProgress: progress,
    };
    const detectStart = performance.now();
    const result = await processScan(image, options, { reuseInput: true });
    timings.detect_seconds = (performance.now() - detectStart) / 1000;
    const mask = result.mask, marks = result.marks;
    progress({ fraction: .94, message: '整理除尘蒙版' });
    const { regions, runs } = connectedRegions(mask);
    const labeled = attributeRegions(regions, runs, marks, image.width, image.height);
    await fs.writeFile(output, runs, { flag: 'wx', mode: 0o600 });
    const component = await fs.readFile(path.join(root, 'runtime/component.json'), 'utf8')
        .then(JSON.parse).catch(error => { if (error.code === 'ENOENT') return null; throw error; });
    const trainingMetadata = {
        inference: { ...(runtimeInfo ? JSON.parse(runtimeInfo) : { provider: process.platform === 'darwin' ? 'coreml-auto' : 'unavailable' }), component },
        sourceSha256,
        settings: optionsChecked(options),
        timings,
        marks: (marks ?? []).filter(m => m.verdict === 'defect').map(m => ({
            kind: m.kind, x0: m.x0, y0: m.y0, x1: m.x1, y1: m.y1,
            area: m.area, learned: m.learned === true || undefined, fibre: m.fibre,
        })),
    };
    progress({ fraction: 1, message: '检测完成' });
    emit({ type: 'review', review: { width: image.width, height: image.height, regions: labeled,
        native: result.stats.native, workingFactor: result.stats.working_factor, trainingMetadata,
        fibreMapPath,
        fibreMapSha256: modelInputs.fibreMap ? createHash('sha256').update(modelInputs.fibreMap).digest('hex') : null } });
}

main().catch(error => {
    modelAbort.abort();
    emit({ type: 'error', message: error.message });
    process.exitCode = 1;
});
