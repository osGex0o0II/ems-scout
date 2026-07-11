'use strict';

const fs = require('fs');
const path = require('path');

const BUILDING_ORDER = ['1号', '2号', '3号', '4号', '5号', '6号'];

function createEnumerationOutputStore({ outputFile, append = false, now = () => new Date() }) {
  const absolute = path.resolve(outputFile);
  let initialized = false;

  function loadExisting() {
    try {
      return JSON.parse(fs.readFileSync(absolute, 'utf8').replace(/^\uFEFF/, ''));
    } catch {
      return { buildings: [] };
    }
  }

  function saveBuilding(buildingResult) {
    const output = append || initialized ? loadExisting() : { buildings: [] };
    initialized = true;
    if (!Array.isArray(output.buildings)) output.buildings = [];
    output.buildings = output.buildings.filter(building => building.building !== buildingResult.building);
    output.buildings.push(buildingResult);
    output.buildings.sort((left, right) =>
      BUILDING_ORDER.indexOf(left.building) - BUILDING_ORDER.indexOf(right.building));
    output.completedAt = now().toISOString();
    writeAtomic(absolute, JSON.stringify(output, null, 2));
    return output;
  }

  return { loadExisting, saveBuilding };
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
