import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { transformStyle } from './map-style-transform.mjs';

/** Ручной генератор стилей подложки: `npm run map-style`. */

const TARGETS = [
  {
    url: 'https://tiles.openfreemap.org/styles/positron',
    file: 'light.json',
    name: 'Городские ресурсы — светлая',
  },
  {
    url: 'https://tiles.openfreemap.org/styles/dark',
    file: 'dark.json',
    name: 'Городские ресурсы — тёмная',
  },
];

const outDir = join(dirname(fileURLToPath(import.meta.url)), '..', 'public', 'map-style');

async function download(url) {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${url}: HTTP ${response.status}`);
  return response.json();
}

async function main() {
  await mkdir(outDir, { recursive: true });
  for (const target of TARGETS) {
    const style = transformStyle(await download(target.url), { name: target.name });
    const file = join(outDir, target.file);
    await writeFile(file, `${JSON.stringify(style, null, 2)}\n`, 'utf8');
    console.log(`${target.file}: ${style.layers.length} слоёв`);
  }
}

main().catch((err) => {
  console.error('Не удалось собрать стили карты:', err instanceof Error ? err.message : err);
  process.exitCode = 1;
});
