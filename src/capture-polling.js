'use strict';

const { checkCardQuality } = require('./rules');
const {
  assessDataQuality,
  isAcceptableCapture,
  isOfflineTemplateStable,
  persistentDeviceAnomalyState,
} = require('./capture-quality');

const RETRY_DELAYS = [200, 500, 1000, 2000, 5000];

function createCapturePolling({
  logger = () => {},
  levels = { DEBUG: 'DEBUG', WARN: 'WARN' },
  log = () => {},
  sleep = ms => new Promise(resolve => setTimeout(resolve, ms)),
  now = () => Date.now(),
} = {}) {
  async function qualityCheckWithProgressiveRetry(_page, extractCards, description, maxAttempts = 5) {
    let prevOfflineTemplate = { signature: '', rounds: 0 };
    let prevDeviceAnomaly = { signature: '', rounds: 0 };
    const startTime = now();

    for (let attempt = 0; attempt < maxAttempts; attempt++) {
      const data = await extractCards();
      const qc = checkCardQuality(data.cards, data);
      const quality = assessDataQuality(data.cards);
      const offlineTemplate = isOfflineTemplateStable(data.cards, qc, prevOfflineTemplate, now() - startTime);
      prevOfflineTemplate = { signature: offlineTemplate.signature, rounds: offlineTemplate.rounds };
      const deviceAnomaly = persistentDeviceAnomalyState(data.cards, data, prevDeviceAnomaly);
      prevDeviceAnomaly = { signature: deviceAnomaly.signature, rounds: deviceAnomaly.rounds };

      if (isAcceptableCapture(data, qc, quality)) {
        logger(levels.DEBUG, 'QUALITY', `${description} OK on attempt ${attempt + 1}: ${qc.details}`);
        data.qualityReason = 'quality_pass';
        return { data, qc, reason: 'quality_pass', attempt: attempt + 1 };
      }

      if (offlineTemplate.accept) {
        logger(levels.WARN, 'QUALITY', `${description} offline template stable after attempt ${attempt + 1}: ${qc.details}`);
        data.qualityReason = 'offline_template_stable';
        return { data, qc: { ...qc, ok: true, offlineTemplateStable: true }, reason: 'offline_template_stable', attempt: attempt + 1 };
      }

      if (deviceAnomaly.accept) {
        logger(levels.WARN, 'QUALITY', `${description} preserving stable device anomalies after attempt ${attempt + 1}: ${deviceAnomaly.details}`);
        data.qualityReason = 'device_anomalies_preserved';
        return { data, qc: { ...qc, ok: true, deviceAnomaliesPreserved: true }, reason: 'device_anomalies_preserved', attempt: attempt + 1 };
      }

      logger(levels.DEBUG, 'QUALITY', `${description} attempt ${attempt + 1} failed: ${qc.details}`, { n: data.cards.length });
      if (attempt < maxAttempts - 1) await sleep(RETRY_DELAYS[attempt] || 1000);
    }

    logger(levels.WARN, 'QUALITY', `${description} max attempts reached, using best available data`);
    return { data: null, qc: null, attempt: maxAttempts };
  }

  async function adaptivePolling(_page, extractCards, deadline, description) {
    const startTime = now();
    const minPollInterval = 200;
    const maxPollInterval = 3000;
    const qualityImprovementThreshold = 0.1;
    let lastQualityScore = 0;
    let pollInterval = minPollInterval;
    let prevOfflineTemplate = { signature: '', rounds: 0 };
    let prevDeviceAnomaly = { signature: '', rounds: 0 };

    while (now() - startTime < deadline) {
      const data = await extractCards();
      const qc = checkCardQuality(data.cards, data);
      const quality = assessDataQuality(data.cards);
      const elapsed = now() - startTime;
      const offlineTemplate = isOfflineTemplateStable(data.cards, qc, prevOfflineTemplate, elapsed);
      prevOfflineTemplate = { signature: offlineTemplate.signature, rounds: offlineTemplate.rounds };
      const deviceAnomaly = persistentDeviceAnomalyState(data.cards, data, prevDeviceAnomaly);
      prevDeviceAnomaly = { signature: deviceAnomaly.signature, rounds: deviceAnomaly.rounds };

      if (isAcceptableCapture(data, qc, quality)) {
        logger(levels.DEBUG, 'QUALITY', `${description} OK after ${elapsed}ms: ${qc.details}`);
        data.qualityReason = 'quality_pass';
        return { data, qc, quality, reason: 'quality_pass' };
      }

      if (offlineTemplate.accept) {
        logger(levels.WARN, 'QUALITY', `${description} offline template stable after ${elapsed}ms: ${qc.details}`);
        data.qualityReason = 'offline_template_stable';
        return { data, qc: { ...qc, ok: true, offlineTemplateStable: true }, quality, reason: 'offline_template_stable' };
      }

      if (deviceAnomaly.accept) {
        logger(levels.WARN, 'QUALITY', `${description} preserving stable device anomalies after ${elapsed}ms: ${deviceAnomaly.details}`);
        data.qualityReason = 'device_anomalies_preserved';
        return { data, qc: { ...qc, ok: true, deviceAnomaliesPreserved: true }, quality, reason: 'device_anomalies_preserved' };
      }

      if (quality.score > lastQualityScore + qualityImprovementThreshold) {
        lastQualityScore = quality.score;
        pollInterval = Math.max(minPollInterval, pollInterval * 0.8);
        logger(levels.DEBUG, 'QUALITY', `${description} quality improving, reducing poll interval to ${pollInterval}ms`, { score: quality.score });
      } else if (quality.score < lastQualityScore - qualityImprovementThreshold) {
        pollInterval = Math.min(maxPollInterval, pollInterval * 1.2);
        logger(levels.DEBUG, 'QUALITY', `${description} quality not improving, increasing poll interval to ${pollInterval}ms`, { score: quality.score });
      }

      lastQualityScore = quality.score;
      await sleep(pollInterval);
    }

    log(`      ${description} timeout after ${now() - startTime}ms`);
    return { data: null, qc: null, quality: null, reason: 'timeout' };
  }

  return { adaptivePolling, qualityCheckWithProgressiveRetry };
}

module.exports = { createCapturePolling };
