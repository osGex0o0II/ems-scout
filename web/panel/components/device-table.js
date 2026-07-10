'use strict';

(function attachDeviceTable(global) {
  const UI = global.UI;
  const Health = global.DeviceHealth;

  function esc(value) {
    return UI.escapeHtml(value);
  }

  function attrJson(value) {
    return esc(JSON.stringify(value));
  }

  function textOrDash(value) {
    const raw = String(value ?? '').trim();
    return raw || '-';
  }

  function field(row, name) {
    return Health.realtimeField(row, name) || '-';
  }

  function badge(text, type = 'muted', extra = '') {
    return UI.badge(text, type, extra);
  }

  function powerType(value) {
    return UI.powerType(value);
  }

  function statusType(value) {
    return UI.statusType(value);
  }

  function areaTypeBadge(value) {
    const text = textOrDash(value);
    const type = text === '公区'
      ? 'info'
      : text === '非公区'
        ? 'success'
        : text === '未匹配'
          ? 'warning'
          : 'muted';
    return badge(text, type);
  }

  function matchStatusBadge(row) {
    const text = row.match_status || '实时详情';
    const type = row.match_override_action === 'ignore_duplicate'
      ? 'muted'
      : row.match_override_action === 'create_virtual' || row.match_override_action === 'classify_only' || row.match_override_action === 'map_to_db'
        ? 'info'
        : 'success';
    return badge(text, type);
  }

  function lockBadge(row) {
    const value = field(row, '集控锁定');
    if (value === '开启') return badge('集控锁定', 'locked');
    if (value === '关闭') return badge('未锁定', 'success');
    if (value === '-' || !value) return badge('-', 'muted');
    return badge(value, 'warning');
  }

  function pointsBadge(row) {
    if (!row.realtime) return badge('无详情', 'muted');
    const valid = Number(row.realtime.realtime_valid_tag_count || 0);
    const total = Number(row.realtime.realtime_tag_count || 0);
    const type = total > 0 && valid >= total ? 'success' : 'warning';
    return badge(`${valid}/${total}`, type);
  }

  function renderTags(tags = []) {
    if (!Array.isArray(tags) || !tags.length) return '';
    return `<div class="tag-list">${tags.map(t => `<span class="tag">${esc(t)}</span>`).join('')}</div>`;
  }

  function renderDeviceCell(row) {
    const dev = row.dev_id || row.meter_id || '';
    return `
      <div class="device-name-cell">
        <strong>${esc(row.name || '-')}</strong>
        <span>${dev ? `设备ID ${esc(dev)}` : '设备ID --'}</span>
        ${renderTags(row.tags)}
      </div>
    `;
  }

  function renderLocationCell(row) {
    const parts = [
      row.building,
      row.floor_label || row.floor,
      row.area_type,
      row.zuo || '',
    ].filter(Boolean);
    return `
      <div class="location-cell">
        <strong>${esc(parts.join(' / ') || '-')}</strong>
        <span>${esc(row.sub_area || '-')} · 页面 ${esc(row.page_name || '-')}</span>
      </div>
    `;
  }

  function renderHealthCell(row) {
    const health = Health.deriveDeviceHealth(row);
    const issues = Health.collectDeviceIssues(row).filter(issue => issue.key !== 'ignored');
    const shortReason = health.status === 'normal'
      ? '无异常'
      : issues.length
        ? issues.slice(0, 2).map(issue => issue.label).join(' / ')
        : health.reason;
    return `
      <div class="health-cell" title="${esc(health.reason)}">
        ${badge(health.label, health.badgeType)}
        <span>${esc(shortReason)}</span>
      </div>
    `;
  }

  function renderPointQualityCell(row) {
    const updatedAt = renderRealtimeUpdatedAt(row);
    const title = updatedAt === '-' ? '' : `实时详情 ${updatedAt}`;
    return `
      <div class="comm-match-cell point-quality-cell" title="${esc(title)}">
        <div class="status-stack compact">
          ${pointsBadge(row)}
          ${matchStatusBadge(row)}
        </div>
        <span class="cell-note">${esc(compactRealtimeUpdatedAt(row))}</span>
      </div>
    `;
  }

  function renderRunStateCell(row) {
    const comm = Health.commState(row);
    const commTitle = comm.raw ? `${comm.label}：${comm.raw}` : comm.label;
    return `
      <div class="status-stack run-state-cell">
        ${badge(commTitle, comm.type)}
        ${badge(textOrDash(field(row, '当前开关机状态')), powerType(field(row, '当前开关机状态')))}
        ${badge(textOrDash(field(row, '系统模式设置')), statusType(field(row, '系统模式设置')))}
        ${badge(textOrDash(field(row, '设定风速')), 'muted')}
        ${lockBadge(row)}
      </div>
    `;
  }

  function renderTempCell(row) {
    const indoor = textOrDash(field(row, '室内温度'));
    const setTemp = textOrDash(field(row, '设定温度'));
    const temp = Health.tempState(row);
    const cls = temp.abnormal ? 'danger' : temp.missing && row.realtime ? 'warning' : '';
    return `<span class="temp-pair ${cls}"><strong>${esc(indoor)}</strong><em>/ ${esc(setTemp)}</em></span>`;
  }

  function renderRealtimeUpdatedAt(row) {
    if (!row.realtime || !row.realtime.source_mtime) return '-';
    const d = new Date(row.realtime.source_mtime);
    if (Number.isNaN(d.getTime())) return row.realtime.source_mtime;
    const pad = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }

  function renderDateTime(value) {
    if (!value) return '-';
    const d = new Date(value);
    if (Number.isNaN(d.getTime())) return value;
    const pad = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }

  function compactRealtimeUpdatedAt(row) {
    if (!row.realtime || !row.realtime.source_mtime) return '-';
    const d = new Date(row.realtime.source_mtime);
    if (Number.isNaN(d.getTime())) return row.realtime.source_mtime;
    const pad = n => String(n).padStart(2, '0');
    return `更新 ${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }

  function renderIssues(row) {
    const issues = Health.collectDeviceIssues(row);
    if (!issues.length) return '<span class="muted">-</span>';
    return `<div class="issue-list">${issues.map(i => badge(i.label, i.type, 'issue-chip')).join('')}</div>`;
  }

  function renderRealtimeMatchControls(row, zuoOptionsMap) {
    if (row.match_override_action === 'ignore_duplicate') return '';
    const key = {
      building: row.building,
      dev_id: row.dev_id || '',
      floor_label: row.floor_label || row.floor || '',
      sub_area: row.sub_area || '',
      page_name: row.page_name || 'default',
      realtime_name: row.name,
    };
    const controls = [];
    const zuos = zuoOptionsMap[row.building] || [];
    const manualZuo = row.zuo_source === 'manual' ? row.zuo : '';
    const manualArea = row.area_type_source === 'manual' ? row.area_type : '';
    if (zuos.length) {
      controls.push(`
        <select data-realtime-zuo="${attrJson(key)}" title="设置设备座号">
          <option value="">座号</option>
          ${zuos.map(z => `<option value="${esc(z)}"${z === manualZuo ? ' selected' : ''}>${esc(z)}</option>`).join('')}
          <option value="__clear">清空座号</option>
        </select>
      `);
    }
    controls.push(`
      <select data-realtime-area="${attrJson(key)}" title="设置设备区域分类">
        <option value="">区域</option>
        <option value="公区"${manualArea === '公区' ? ' selected' : ''}>公区</option>
        <option value="非公区"${manualArea === '非公区' ? ' selected' : ''}>非公区</option>
        <option value="未匹配"${manualArea === '未匹配' ? ' selected' : ''}>未匹配</option>
        <option value="__clear">清空区域</option>
      </select>
    `);
    controls.push(`
      <button class="ghost small" data-realtime-ignore="${attrJson({ ...key, ignored: row.match_override_action === 'ignore_duplicate' })}">
        ${row.match_override_action === 'ignore_duplicate' ? '取消忽略' : '忽略'}
      </button>
    `);
    return controls.join('');
  }

  function renderActions(row, index, options) {
    return `
      <div class="row-actions">
        <button class="ghost small" data-device-detail="${index}">详情</button>
        <select data-tag-select="${attrJson({ building: row.building, name: row.name })}">
          <option value="">打标签</option>
          ${(options.commonTags || []).map(t => `<option value="${esc(t)}">${esc(t)}</option>`).join('')}
        </select>
        ${renderRealtimeMatchControls(row, options.zuoOptions || {})}
        <button class="ghost small" data-note-card="${attrJson({ building: row.building, name: row.name, note: row.note || '' })}">备注</button>
      </div>
    `;
  }

  function rowClass(row) {
    const health = Health.deriveDeviceHealth(row);
    if (health.badgeType === 'danger') return 'row-danger';
    if (health.badgeType === 'warning') return 'row-warn';
    return '';
  }

  function renderRow(row, index, options = {}) {
    return `
      <tr class="${rowClass(row)}">
        <td>${renderDeviceCell(row)}</td>
        <td>${renderLocationCell(row)}</td>
        <td>${renderHealthCell(row)}</td>
        <td>${renderPointQualityCell(row)}</td>
        <td>${renderRunStateCell(row)}</td>
        <td>${renderTempCell(row)}</td>
        <td>${renderIssues(row)}</td>
        <td class="actions-col">${renderActions(row, index, options)}</td>
      </tr>
    `;
  }

  function renderKeyValueRows(rows) {
    return rows.map(([label, value]) => `
      <div class="detail-kv">
        <span>${esc(label)}</span>
        <strong>${esc(value === null || value === undefined || value === '' ? '--' : value)}</strong>
      </div>
    `).join('');
  }

  function renderFieldTable(title, obj = {}) {
    const entries = Object.entries(obj || {});
    return `
      <section class="detail-section">
        <h4>${esc(title)}</h4>
        ${entries.length ? `
          <div class="detail-field-table">
            ${entries.map(([k, v]) => `<span>${esc(k)}</span><strong>${esc(v === '' ? '--' : v)}</strong>`).join('')}
          </div>
        ` : '<div class="muted">--</div>'}
      </section>
    `;
  }

  function renderDetail(row) {
    const health = Health.deriveDeviceHealth(row);
    const realtime = row.realtime || {};
    return `
      <div class="device-detail-head">
        <div>
          <h3>${esc(row.name || '-')}</h3>
          <span>${esc([row.building, row.floor_label || row.floor, row.sub_area, row.page_name].filter(Boolean).join(' / '))}</span>
        </div>
        ${badge(health.label, health.badgeType)}
      </div>
      <div class="device-detail-reason">${esc(health.reason)}</div>
      <section class="detail-section">
        <h4>基础信息</h4>
        <div class="detail-grid">
          ${renderKeyValueRows([
            ['设备名称', row.name],
            ['设备ID', row.dev_id || row.meter_id],
            ['楼栋', row.building],
            ['楼层', row.floor_label || row.floor],
            ['子区', row.sub_area],
            ['页面', row.page_name],
            ['区域', row.area_type],
            ['座号', row.zuo || '--'],
            ['座号来源', row.zuo_source_label],
            ['标签', (row.tags || []).join('、') || '--'],
            ['备注', row.note || '--'],
          ])}
        </div>
      </section>
      <section class="detail-section">
        <h4>实时详情字段</h4>
        <div class="detail-grid">
          ${renderKeyValueRows([
            ['详情文件', realtime.source_file],
            ['详情时间', renderRealtimeUpdatedAt(row)],
            ['dev_id', realtime.dev_id || row.dev_id],
            ['meter_id', realtime.meter_id || row.meter_id],
            ['rtu_id', realtime.rtu_id || row.rtu_id],
            ['通讯状态', row.comm_state || Health.commState(row).state],
            ['通讯来源', row.comm_state_source || realtime.card_state_source || '--'],
            ['卡片状态', realtime.card_comm || row.card_comm || '--'],
            ['卡片 indicator', realtime.card_indicator || row.card_indicator || '--'],
            ['点位完整度', row.realtime ? `${realtime.realtime_valid_tag_count || 0}/${realtime.realtime_tag_count || 0}` : '--'],
            ['详情错误', realtime.error || '--'],
            ['默认值疑似', realtime.default_like ? '是' : '否'],
          ])}
        </div>
      </section>
      <section class="detail-section">
        <h4>兼容信息</h4>
        <div class="detail-grid">
          ${renderKeyValueRows([
            ['状态', row.match_status || '详情设备'],
            ['覆盖动作', row.match_override_action || '--'],
            ['覆盖记录ID', row.realtime_override_id || '--'],
            ['exact_key', realtime.exact_key || '--'],
            ['name_key', realtime.name_key || '--'],
          ])}
        </div>
      </section>
      <section class="detail-section">
        <h4>质量 / 异常提示</h4>
        ${renderIssues(row)}
      </section>
      ${renderFieldTable('实时字段预览', realtime.fields)}
      ${renderFieldTable('原始字段预览', realtime.raw_fields)}
    `;
  }

  global.DeviceTable = {
    renderRow,
    renderDetail,
    renderRealtimeUpdatedAt,
    renderIssues,
    areaTypeBadge,
    matchStatusBadge,
    lockBadge,
    pointsBadge,
  };
})(window);
