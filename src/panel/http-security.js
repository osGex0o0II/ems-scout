'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const MAX_JSON_BODY_BYTES = 1024 * 1024;

class HttpRequestError extends Error {
  constructor(statusCode, message) {
    super(message);
    this.name = 'HttpRequestError';
    this.statusCode = statusCode;
  }
}

function hostParts(hostHeader) {
  try {
    const url = new URL(`http://${String(hostHeader || '')}`);
    return { hostname: url.hostname.toLowerCase(), port: url.port };
  } catch {
    return null;
  }
}

function isLoopbackHost(hostHeader) {
  const parts = hostParts(hostHeader);
  return Boolean(parts && ['127.0.0.1', 'localhost', '[::1]'].includes(parts.hostname));
}

function tokensEqual(actual, expected) {
  const left = Buffer.from(String(actual || ''), 'utf8');
  const right = Buffer.from(String(expected || ''), 'utf8');
  return left.length === right.length && crypto.timingSafeEqual(left, right);
}

function authorizeApiRequest(req, sessionToken) {
  const host = req.headers.host;
  if (!isLoopbackHost(host)) throw new HttpRequestError(403, 'Loopback Host required');
  if (!tokensEqual(req.headers['x-ems-panel-token'], sessionToken)) {
    throw new HttpRequestError(403, 'Invalid panel session');
  }

  const method = String(req.method || 'GET').toUpperCase();
  const origin = req.headers.origin;
  if (['GET', 'HEAD', 'OPTIONS'].includes(method) || !origin) return;

  const expected = hostParts(host);
  let actual;
  try {
    const url = new URL(origin);
    actual = { protocol: url.protocol, hostname: url.hostname.toLowerCase(), port: url.port };
  } catch {
    throw new HttpRequestError(403, 'Invalid Origin');
  }
  if (actual.protocol !== 'http:' || actual.hostname !== expected.hostname || actual.port !== expected.port) {
    throw new HttpRequestError(403, 'Cross-origin request rejected');
  }
}

function readJsonBody(req, maxBytes = MAX_JSON_BODY_BYTES) {
  const declared = req.headers['content-length'];
  if (declared !== undefined) {
    const bytes = Number(declared);
    if (!Number.isSafeInteger(bytes) || bytes < 0) {
      return Promise.reject(new HttpRequestError(400, 'Invalid Content-Length'));
    }
    if (bytes > maxBytes) {
      req.resume();
      return Promise.reject(new HttpRequestError(413, 'Request body too large'));
    }
  }

  return new Promise((resolve, reject) => {
    const chunks = [];
    let bytes = 0;
    let settled = false;
    const fail = error => {
      if (settled) return;
      settled = true;
      reject(error);
    };
    req.on('data', chunk => {
      if (settled) return;
      const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      bytes += buffer.length;
      if (bytes > maxBytes) {
        fail(new HttpRequestError(413, 'Request body too large'));
        return;
      }
      chunks.push(buffer);
    });
    req.on('end', () => {
      if (settled) return;
      settled = true;
      if (bytes === 0) return resolve({});
      const type = String(req.headers['content-type'] || '').split(';', 1)[0].trim().toLowerCase();
      if (type !== 'application/json') {
        reject(new HttpRequestError(415, 'Content-Type must be application/json'));
        return;
      }
      try {
        resolve(JSON.parse(Buffer.concat(chunks, bytes).toString('utf8')));
      } catch {
        reject(new HttpRequestError(400, 'Invalid JSON body'));
      }
    });
    req.on('error', error => fail(new HttpRequestError(400, error.message)));
  });
}

function isWithin(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative));
}

function resolveStaticPath(root, requestPath) {
  let decoded;
  try {
    decoded = decodeURIComponent(requestPath);
  } catch {
    throw new HttpRequestError(400, 'Invalid URL path');
  }
  const realRoot = fs.realpathSync(root);
  const candidate = path.resolve(realRoot, decoded.replace(/^[/\\]+/, ''));
  if (!isWithin(realRoot, candidate)) throw new HttpRequestError(403, 'Forbidden');
  if (!fs.existsSync(candidate)) return candidate;
  const realCandidate = fs.realpathSync(candidate);
  if (!isWithin(realRoot, realCandidate)) throw new HttpRequestError(403, 'Forbidden');
  return realCandidate;
}

function createSessionToken() {
  return crypto.randomBytes(32).toString('base64url');
}

module.exports = {
  HttpRequestError,
  authorizeApiRequest,
  createSessionToken,
  isLoopbackHost,
  readJsonBody,
  resolveStaticPath,
};
