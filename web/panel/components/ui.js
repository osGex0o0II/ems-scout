'use strict';

(function attachPanelUi(global) {
  function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, ch => ({
      '&': '&amp;',
      '<': '&lt;',
      '>': '&gt;',
      '"': '&quot;',
      "'": '&#39;',
    }[ch]));
  }

  function cx(...parts) {
    return parts.filter(Boolean).join(' ');
  }

  function badge(text, type = 'muted', extraClass = '') {
    const label = text === null || text === undefined || text === '' ? '--' : text;
    return `<span class="${cx('badge', type, extraClass)}">${escapeHtml(label)}</span>`;
  }

  function metricCard({ label, value = '--', meta = '', type = '', accent = '' } = {}) {
    return `
      <div class="${cx('kpi', 'metric-card', type, accent)}">
        <span>${escapeHtml(label || '')}</span>
        <strong>${escapeHtml(value === null || value === undefined || value === '' ? '--' : value)}</strong>
        ${meta ? `<em>${escapeHtml(meta)}</em>` : ''}
      </div>
    `;
  }

  function panelCard({ title, meta = '', body = '', extraClass = '' } = {}) {
    return `
      <section class="${cx('panel', 'panel-card', extraClass)}">
        <div class="panel-head">
          <h3>${escapeHtml(title || '')}</h3>
          ${meta ? `<span>${meta}</span>` : ''}
        </div>
        ${body}
      </section>
    `;
  }

  function emptyState({ title = '暂无数据', detail = '' } = {}) {
    return `
      <div class="empty-state">
        <strong>${escapeHtml(title)}</strong>
        ${detail ? `<span>${escapeHtml(detail)}</span>` : ''}
      </div>
    `;
  }

  function statusType(value) {
    const v = String(value ?? '').trim();
    if (!v || v === '-' || v === '--' || v === '未知' || v === '无数据') return 'muted';
    if (['正常', '在线', '已完成', '完成', 'OK', '数据可用', '已匹配', '点位完整', '关机'].includes(v)) return 'success';
    if (['开机', '运行中', '处理中', '采集中', '启动中', '手动匹配', '虚拟纳管', '人工分类'].includes(v)) return 'info';
    if (['警告', '建议复核', '需排查', '质量问题', '疑似异常', '未入库/键错位', '未匹配', '点位不完整', '待导入'].includes(v)) return 'warning';
    if (['故障', '失败', '阻止导入', '严重异常', '异常', '详情字段异常', '字段异常'].includes(v)) return 'danger';
    if (['离线', '已忽略重复'].includes(v)) return 'muted';
    if (v.includes('锁定')) return 'locked';
    if (v.includes('异常') || v.includes('失败') || v.includes('阻止')) return 'danger';
    if (v.includes('问题') || v.includes('疑似') || v.includes('未')) return 'warning';
    return 'info';
  }

  function powerType(value) {
    const v = String(value ?? '').trim().toUpperCase();
    if (v === '开机' || v === 'ON') return 'info';
    if (v === '关机' || v === 'OFF') return 'success';
    if (v === '离线') return 'muted';
    return 'muted';
  }

  function taskStatusType(status) {
    const s = String(status ?? '').trim().toLowerCase();
    if (s === 'done' || s === 'success') return 'success';
    if (s === 'failed' || s === 'error') return 'danger';
    if (s === 'running' || s === 'stopping') return 'info';
    return 'muted';
  }

  function qualityType(severity) {
    const s = String(severity ?? '').trim().toUpperCase();
    if (s === 'P1') return 'danger';
    if (s === 'P2' || s === 'P3') return 'warning';
    if (s === 'OK') return 'success';
    if (s === 'INFO') return 'info';
    return 'muted';
  }

  global.UI = {
    escapeHtml,
    cx,
    badge,
    metricCard,
    panelCard,
    emptyState,
    statusType,
    powerType,
    taskStatusType,
    qualityType,
  };
})(window);
