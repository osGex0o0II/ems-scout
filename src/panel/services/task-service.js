'use strict';

const fs = require('fs');
const path = require('path');
const { normalizeTaskError } = require('../rules/task-error-rules');

const STAGE_TEXT = {
  idle: '未开始',
  preflight: '正在检查环境',
  starting_browser: '正在启动内置浏览器',
  collecting_inventory: '正在校验设备清单',
  collecting_realtime: '正在采集实时详情',
  auditing_quality: '正在执行质量审计',
  completed: '已完成',
  failed: '失败',
  stopped: '已停止',
};

function createTaskService(options) {
  const ROOT = options.root;
  const OUT_DIR = options.outDir;
  const BLDG_ORDER = options.buildings || [];
  const realtimeService = options.realtimeService;
  const qualityService = options.qualityService;
  const realtimeBrowser = options.realtimeBrowser || {};
  const latestCollectionMeta = options.latestCollectionMeta;
  const realtimeSummary = options.realtimeSummary;

  function edgeCandidates() {
    const env = process.env;
    return [
      env.EDGE_PATH,
      path.join(env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Microsoft\\Edge\\Application\\msedge.exe'),
      path.join(env.ProgramFiles || 'C:\\Program Files', 'Microsoft\\Edge\\Application\\msedge.exe'),
      path.join(env.LocalAppData || '', 'Microsoft\\Edge\\Application\\msedge.exe'),
    ].filter(Boolean);
  }

  function findEdgePath() {
    return edgeCandidates().find(file => file && fs.existsSync(file)) || '';
  }

  function addCheck(checks, check) {
    checks.push({
      key: check.key,
      label: check.label,
      status: check.status || 'unknown',
      message: check.message || '',
      detail: check.detail || '',
      suggestion: check.suggestion || '',
    });
  }

  function qualitySummaryText(report) {
    const s = report && report.summary || {};
    if (!report) return '尚未生成质量审计';
    const issueCount = Number(s.issue_count || 0);
    const collectionErrors = Number(s.collection_errors || 0);
    const deviceAnomalyRows = Number(s.device_anomaly_rows || 0);
    if (issueCount > 0) {
      const parts = [];
      if (collectionErrors) parts.push(`采集错误 ${collectionErrors}`);
      if (deviceAnomalyRows) parts.push(`字段复核 ${deviceAnomalyRows}`);
      return parts.length ? `${issueCount} 项待复核：${parts.join(' / ')}` : `${issueCount} 项待复核`;
    }
    return '质量审计未发现阻断问题';
  }

  function buildPreflight() {
    const checks = [];
    const emsUrl = realtimeBrowser.DEFAULT_EMS_URL || process.env.EMS_URL || 'http://172.29.248.4:8000/ui';
    addCheck(checks, {
      key: 'ems_url',
      label: 'EMS 页面',
      status: emsUrl ? 'ok' : 'error',
      message: emsUrl ? 'EMS 页面地址已配置' : '未配置 EMS 页面地址',
      detail: emsUrl,
      suggestion: emsUrl ? '' : '请设置 EMS_URL，或使用默认 EMS 页面地址。',
    });

    const edgePath = findEdgePath();
    addCheck(checks, {
      key: 'edge_path',
      label: 'Edge 浏览器',
      status: edgePath ? 'ok' : 'error',
      message: edgePath ? '已找到 Edge 浏览器' : '未找到 Microsoft Edge',
      detail: edgePath || edgeCandidates().join(' ; '),
      suggestion: edgePath ? '' : '请安装 Microsoft Edge，或设置 EDGE_PATH 指向 msedge.exe。',
    });

    const profile = realtimeBrowser.REALTIME_EDGE_PROFILE || path.join(OUT_DIR, '.edge_profile_realtime');
    addCheck(checks, {
      key: 'edge_profile',
      label: '浏览器配置目录',
      status: fs.existsSync(profile) ? 'warning' : 'ok',
      message: fs.existsSync(profile)
        ? '配置目录存在，若采集失败请确认没有旧窗口占用'
        : '配置目录将在首次采集时创建',
      detail: path.relative(ROOT, profile),
      suggestion: fs.existsSync(profile) ? '如果提示目录被占用，请关闭上一次自动打开的 Edge 窗口。' : '',
    });

    const port = Number(realtimeBrowser.DEFAULT_REALTIME_CDP_PORT || process.env.REALTIME_CDP_PORT || 9333);
    addCheck(checks, {
      key: 'realtime_cdp_port',
      label: '实时采集会话端口',
      status: 'ok',
      message: `会话端口 ${port}`,
      detail: `REALTIME_CDP_PORT=${port}`,
      suggestion: '',
    });

    addCheck(checks, {
      key: 'login_status',
      label: 'EMS 登录状态',
      status: 'unknown',
      message: '运行前无法可靠判断登录态',
      detail: '采集启动后会打开 EMS 页面，未登录时请在窗口内完成登录',
      suggestion: '登录状态未知不会阻止采集。',
    });

    let rt = null;
    try {
      rt = realtimeSummary ? realtimeSummary() : null;
    } catch {
      rt = null;
    }
    const latestRows = Number(rt && rt.total_rows || 0);
    addCheck(checks, {
      key: 'latest_realtime',
      label: '最近实时数据',
      status: latestRows > 0 ? 'ok' : 'warning',
      message: latestRows > 0 ? `最近采集设备数 ${latestRows}` : '尚未找到实时详情数据',
      detail: rt && rt.latest_mtime ? rt.latest_mtime : '',
      suggestion: latestRows > 0 ? '' : '可以直接开始采集，完成后这里会显示最新设备数。',
    });

    let quality = null;
    try {
      quality = qualityService && qualityService.loadQualityReport ? qualityService.loadQualityReport() : null;
    } catch {
      quality = null;
    }
    const issueCount = Number(quality && quality.summary && quality.summary.issue_count || 0);
    addCheck(checks, {
      key: 'quality',
      label: '质量审计',
      status: quality ? (issueCount > 0 ? 'warning' : 'ok') : 'unknown',
      message: quality ? qualitySummaryText(quality) : '无法确认质量审计状态',
      detail: quality && (quality.source_file || quality.generated_at || quality.generated_at_local) || '',
      suggestion: quality
        ? issueCount > 0 ? '质量问题不一定代表采集失败，请在质量审计页查看待复核项。' : ''
        : '采集完成后会自动生成质量审计。',
    });

    let latestCollection = null;
    try {
      latestCollection = latestCollectionMeta ? latestCollectionMeta() : null;
    } catch {
      latestCollection = null;
    }
    if (latestCollection) {
      addCheck(checks, {
        key: 'latest_inventory',
        label: '最近设备清单',
        status: latestCollection.parse_error ? 'warning' : 'ok',
        message: latestCollection.parse_error
          ? '最近设备清单无法解析'
          : `最近设备清单 ${latestCollection.card_count || 0} 台`,
        detail: latestCollection.completed_at || latestCollection.mtime || '',
        suggestion: latestCollection.pending_import ? '存在待导入采集文件，实时详情采集仍可继续。' : '',
      });
    }

    const blockingCount = checks.filter(c => c.status === 'error').length;
    const warningCount = checks.filter(c => c.status === 'warning').length;
    return {
      ok: blockingCount === 0,
      checks,
      summary: {
        canStart: blockingCount === 0,
        blockingCount,
        warningCount,
        checkedAt: new Date().toISOString(),
      },
    };
  }

  function inferStage(task) {
    if (!task) return 'idle';
    if (task.status === 'done') return 'completed';
    if (task.status === 'failed') return 'failed';
    if (task.status === 'stopped' || task.status === 'stopping') return 'stopped';
    const runningStep = (task.steps || []).find(s => s.status === 'running');
    const label = String(runningStep && runningStep.label || '').toLowerCase();
    const logs = (task.logs || []).slice(-80).join('\n');
    if (/质量|audit/.test(label) || /质量审计|audit-realtime-data/.test(logs)) return 'auditing_quality';
    if (/实时详情|realtime/.test(label) || /collect-realtime|实时详情批量/.test(logs)) return 'collecting_realtime';
    if (/校验|清单|validate|inventory/.test(label) || /刷新卡片状态|设备清单/.test(logs)) return 'collecting_inventory';
    if (/browser|edge|persistent|cdp|启动/.test(logs)) return 'starting_browser';
    return task.status === 'running' ? 'collecting_realtime' : 'idle';
  }

  function elapsedMs(task) {
    if (!task || !task.started_at) return 0;
    const start = new Date(task.started_at).getTime();
    const end = task.ended_at ? new Date(task.ended_at).getTime() : Date.now();
    if (!Number.isFinite(start) || !Number.isFinite(end)) return 0;
    return Math.max(0, end - start);
  }

  function progressText(task, stage) {
    if (!task) return '当前没有运行中的任务';
    if (task.progress && task.progress.message) return task.progress.message;
    const runningStep = (task.steps || []).find(s => s.status === 'running');
    if (runningStep) return `${runningStep.label}进行中，详情见日志`;
    if (stage === 'completed') return '任务已完成';
    if (stage === 'failed') return '任务失败，请查看处理建议和详细日志';
    if (stage === 'stopped') return '任务已停止';
    return '采集中，详情见日志';
  }

  function decorateTask(task) {
    if (!task) return null;
    const stage = inferStage(task);
    const normalizedError = normalizeTaskError(task.error || taskFailureLine(task));
    return {
      ...task,
      ui_status: stage,
      statusText: STAGE_TEXT[stage] || task.status || '未知',
      progressText: progressText(task, stage),
      startedAt: task.started_at || null,
      finishedAt: task.ended_at || null,
      elapsedMs: elapsedMs(task),
      canStart: !['running', 'stopping'].includes(task.status),
      canStop: ['running', 'stopping'].includes(task.status),
      normalizedError,
    };
  }

  function taskFailureLine(task) {
    if (!task || task.status !== 'failed') return '';
    return (task.logs || []).slice(-60).reverse().find(line => /失败|failed|error|timeout|未找到|无法/i.test(line)) || '';
  }

  return {
    buildPreflight,
    decorateTask,
    normalizeTaskError,
    STAGE_TEXT,
  };
}

module.exports = {
  createTaskService,
  STAGE_TEXT,
};
