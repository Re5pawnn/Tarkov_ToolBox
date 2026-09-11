const fs = require("node:fs");
const path = require("node:path");
const { Resvg } = require("@resvg/resvg-js");

const projectRoot = path.resolve(process.argv[2] ?? path.join(__dirname, ".."));
const targetMapId = String(process.argv[3] ?? "").trim();
const manifestPath = path.join(projectRoot, "map-layers.json");
const baseDirectory = path.join(projectRoot, "assets", "maps", "native-cache");
const layerDirectory = path.join(projectRoot, "assets", "maps", "layers");

function readPngSize(filePath) {
  const header = fs.readFileSync(filePath);
  if (
    header.length < 24 ||
    header[0] !== 0x89 ||
    header.toString("ascii", 1, 4) !== "PNG"
  ) {
    throw new Error(`Expected a PNG image: ${filePath}`);
  }
  return {
    width: header.readUInt32BE(16),
    height: header.readUInt32BE(20),
  };
}

function escapeAttribute(value) {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll('"', "&quot;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;");
}

function prepareLayerSvg(svg, layerName, availableLayerNames, width, height) {
  const rootMatch = /<svg\b[^>]*>/i.exec(svg);
  if (!rootMatch) throw new Error("SVG root element is missing.");

  let root = rootMatch[0]
    .replace(/\swidth="[^"]*"/i, "")
    .replace(/\sheight="[^"]*"/i, "")
    .replace(/\spreserveAspectRatio="[^"]*"/i, "");
  root = root.replace(
    /<svg\b/i,
    `<svg width="${width}" height="${height}" preserveAspectRatio="none"`,
  );

  const selector = escapeAttribute(layerName);
  const explicitLayerSelectors = availableLayerNames
    .map((name) => {
      const escaped = escapeAttribute(name);
      return `[data-layer="${escaped}"],[id="${escaped}"]`;
    })
    .join(",");
  const visibilityStyle =
    `<style id="tarkov-map-layer-export">` +
    `[data-layer],${explicitLayerSelectors}{display:none!important}` +
    `[data-layer="${selector}"],[id="${selector}"]{display:inline!important}` +
    `</style>`;

  return (
    svg.slice(0, rootMatch.index) +
    root +
    visibilityStyle +
    svg.slice(rootMatch.index + rootMatch[0].length)
  );
}

function render(svg, layerName, availableLayerNames, width, height) {
  const prepared = prepareLayerSvg(
    svg,
    layerName,
    availableLayerNames,
    width,
    height,
  );
  const renderer = new Resvg(prepared, {
    background: "rgba(0, 0, 0, 0)",
    shapeRendering: 2,
    textRendering: 2,
    imageRendering: 0,
  });
  const image = renderer.render();
  if (image.width !== width || image.height !== height) {
    throw new Error(
      `${layerName} rendered at ${image.width}x${image.height}; expected ${width}x${height}.`,
    );
  }
  return image.asPng();
}

async function main() {
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  fs.mkdirSync(layerDirectory, { recursive: true });

  const pendingWrites = [];
  for (const map of manifest.maps) {
    if (targetMapId && map.id !== targetMapId) continue;
    if (!map.surface?.svgPath || !map.surface?.svgLayer) continue;

    const basePath = path.join(baseDirectory, `${map.id}.png`);
    const { width, height } = fs.existsSync(basePath)
      ? readPngSize(basePath)
      : { width: Number(map.outputWidth), height: Number(map.outputHeight) };
    if (!Number.isFinite(width) || width <= 0 || !Number.isFinite(height) || height <= 0) {
      throw new Error(`Missing output dimensions for ${map.id}.`);
    }
    const response = await fetch(map.surface.svgPath);
    if (!response.ok) {
      throw new Error(
        `Unable to download ${map.surface.svgPath}: HTTP ${response.status}`,
      );
    }
    const svg = await response.text();
    const availableLayerNames = [
      map.surface.svgLayer,
      ...map.layers.map((layer) => layer.svgLayer).filter(Boolean),
    ];

    pendingWrites.push({
      destination: basePath,
      data: render(
        svg,
        map.surface.svgLayer,
        availableLayerNames,
        width,
        height,
      ),
      description: `${map.id}/surface`,
    });

    for (const layer of map.layers) {
      if (!layer.svgLayer) continue;
      pendingWrites.push({
        destination: path.join(layerDirectory, layer.image),
        data: render(svg, layer.svgLayer, availableLayerNames, width, height),
        description: `${map.id}/${layer.id}`,
      });
    }
  }

  for (const item of pendingWrites) {
    const temporaryPath = `${item.destination}.next`;
    fs.writeFileSync(temporaryPath, item.data);
    fs.renameSync(temporaryPath, item.destination);
    console.log(`Rendered ${item.description}`);
  }
  console.log(`Rendered ${pendingWrites.length} SVG map assets.`);
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
