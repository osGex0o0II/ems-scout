'use strict';

const fs = require('fs');
const path = require('path');

const BUILDING_ORDER = ['1号', '2号', '3号', '4号', '5号', '6号'];

function createEnumerationOutputStore({ outputFile, append = false, now = () => new Date() }) {
  const absolute = path.resolve(outputFile);
  const extension = path.extname(absolute);
  const recoveryFile = path.join(
    path.dirname(absolute),
    `${path.basename(absolute, extension)}.recovery${extension || '.json'}`);

  function loadExisting() {
    try {
      return JSON.parse(fs.readFileSync(absolute, 'utf8').replace(/^\uFEFF/, ''));
    } catch (error) {
      if (error && error.code === 'ENOENT') return { buildings: [] };
      if (error instanceof SyntaxError) throw new Error('Existing enumeration output is not valid JSON.', { cause: error });
      throw error;
    }
  }

  function saveRun(buildingResults) {
    const incoming = Array.isArray(buildingResults) ? buildingResults : [];
    const output = append ? loadExisting() : { buildings: [] };
    if (!output || typeof output !== 'object' || Array.isArray(output) || !Array.isArray(output.buildings)) {
      throw new Error('Existing enumeration output must contain a buildings array.');
    }
    const selected = new Set(incoming.map(building => building.building));
    output.buildings = output.buildings.filter(building => !selected.has(building.building));
    output.buildings.push(...incoming);
    output.buildings.sort((left, right) =>
      BUILDING_ORDER.indexOf(left.building) - BUILDING_ORDER.indexOf(right.building));
    output.completedAt = now().toISOString();
    writeAtomic(absolute, JSON.stringify(output, null, 2));
    return output;
  }

  function saveRecapture(buildingResults, options = {}) {
    const incoming = Array.isArray(buildingResults) ? buildingResults : [];
    const output = loadExisting();
    if (!output || typeof output !== 'object' || Array.isArray(output) || !Array.isArray(output.buildings)) {
      throw new Error('Existing enumeration output must contain a buildings array.');
    }
    if (incoming.length === 0) {
      throw new Error('Recapture output must contain at least one building.');
    }

    const selectedBuildings = [];
    for (const capturedBuilding of incoming) {
      const buildingName = capturedBuilding && capturedBuilding.building;
      const existingBuilding = output.buildings.find(building => building.building === buildingName);
      if (!existingBuilding) {
        throw new Error(`Recapture building ${buildingName || '(unknown)'} was not found in formal output.`);
      }
      if (!Array.isArray(existingBuilding.subAreas) || !Array.isArray(capturedBuilding.subAreas)) {
        throw new Error(`Recapture building ${buildingName} must contain a subAreas array.`);
      }

      for (const capturedSubArea of capturedBuilding.subAreas) {
        const index = existingBuilding.subAreas.findIndex(existingSubArea =>
          existingSubArea.floor === capturedSubArea.floor &&
          existingSubArea.x === capturedSubArea.x &&
          existingSubArea.y === capturedSubArea.y);
        if (index < 0) {
          throw new Error(
            `Recapture target ${buildingName}:${capturedSubArea.x}:${capturedSubArea.y} was not found in formal output.`);
        }
        existingBuilding.subAreas[index] = capturedSubArea;
      }
      selectedBuildings.push(buildingName);
    }

    output.completedAt = now().toISOString();
    if (typeof options.validate === 'function') {
      const validation = options.validate(output, [...new Set(selectedBuildings)]);
      if (validation === false || (validation && validation.ok === false)) {
        throw new Error('Merged recapture output failed validation.');
      }
    }
    writeAtomic(absolute, JSON.stringify(output, null, 2));
    return output;
  }

  function saveRecovery(buildingResult) {
    const output = {
      buildings: [buildingResult],
      recoveredAt: now().toISOString(),
    };
    writeAtomic(recoveryFile, JSON.stringify(output, null, 2));
    return recoveryFile;
  }

  return { loadExisting, saveRun, saveRecapture, saveRecovery };
}

function writeAtomic(outputFile, text) {
  fs.mkdirSync(path.dirname(outputFile), { recursive: true });
  const temporary = `${outputFile}.tmp-${process.pid}-${Date.now()}`;
  try {
    fs.writeFileSync(temporary, text, { encoding: 'utf8', flag: 'wx' });
    fs.renameSync(temporary, outputFile);
  } finally {
    fs.rmSync(temporary, { force: true });
  }
}

module.exports = { createEnumerationOutputStore };
