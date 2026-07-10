'use strict';

const RULES = [
  {
    key: 'edge_profile_busy',
    severity: 'error',
    title: '浏览器配置目录被占用',
    pattern: /user data dir|profile|already in use|正在使用|占用|lock|配置目录正在被占用/i,
    message: '上一次自动采集窗口可能还没有完全关闭，导致内置浏览器无法启动。',
    suggestion: '请关闭自动打开的 Edge 窗口后重试；仍失败时在诊断模式中切换为连接已有 Edge CDP。',
  },
  {
    key: 'ems_unreachable',
    severity: 'error',
    title: 'EMS 页面无法访问',
    pattern: /ERR_CONNECTION|ECONNREFUSED|ENOTFOUND|ETIMEDOUT|navigation|goto|无法访问|无法连接.*EMS|EMS URL/i,
    message: '采集程序无法打开 EMS 页面，可能是网络、VPN 或 EMS 服务不可达。',
    suggestion: '请确认当前电脑能在浏览器中打开 EMS 页面，再重新开始采集。',
  },
  {
    key: 'login_expired',
    severity: 'warning',
    title: 'EMS 登录态失效',
    pattern: /未登录|登录|login|password|EMS 页面未登录|未就绪/i,
    message: '内置浏览器已打开，但 EMS 页面还没有进入可采集状态。',
    suggestion: '请在自动打开的 Edge 窗口完成登录，然后重新开始采集。',
  },
  {
    key: 'detail_modal_missing',
    severity: 'error',
    title: '实时详情弹窗定位失败',
    pattern: /详情弹窗|detail modal|modal|dialog|定位失败|找不到.*详情|实时详情.*失败/i,
    message: '采集程序没有定位到设备实时详情弹窗，可能是页面结构变化或页面未加载完整。',
    suggestion: '请刷新 EMS 页面后重试；如果持续出现，需要复核实时详情采集脚本的页面选择器。',
  },
  {
    key: 'timeout',
    severity: 'error',
    title: '采集等待超时',
    pattern: /timeout|timed out|超时|等待.*超时/i,
    message: '采集过程中等待页面或实时数据返回超时。',
    suggestion: '请确认 EMS 页面响应正常；可以在诊断模式中适当增大单批超时时间或降低批量设备数。',
  },
  {
    key: 'json_missing',
    severity: 'error',
    title: '采集结果文件未生成',
    pattern: /enum_full_v5\.json|realtime_.*latest\.json|JSON.*未生成|no such file|ENOENT.*json/i,
    message: '采集脚本结束后没有生成预期的数据文件。',
    suggestion: '请查看详细日志中最早的错误原因，修复后重新采集。',
  },
  {
    key: 'quality_missing',
    severity: 'warning',
    title: '质量审计文件未生成',
    pattern: /quality.*json|quality_report|质量审计.*未生成|audit.*missing/i,
    message: '采集后没有找到质量审计结果，设备数据可能已生成但缺少复核摘要。',
    suggestion: '可以重新执行质量审计；如果采集数据本身异常，请重新采集。',
  },
  {
    key: 'user_stopped',
    severity: 'info',
    title: '任务已手动停止',
    pattern: /任务已停止|用户.*停止|stopped|SIGTERM|taskkill/i,
    message: '本次采集任务已由用户停止。',
    suggestion: '如需继续，请重新开始采集。',
  },
  {
    key: 'port_busy',
    severity: 'error',
    title: '浏览器调试端口被占用',
    pattern: /EADDRINUSE|address already in use|端口.*占用|remote-debugging-port/i,
    message: '采集需要的浏览器调试端口已经被其他进程占用。',
    suggestion: '请关闭旧采集窗口或占用端口的进程，然后重新开始采集。',
  },
  {
    key: 'edge_path_missing',
    severity: 'error',
    title: '未找到 Microsoft Edge',
    pattern: /未找到 Microsoft Edge|EDGE_PATH|msedge\.exe|executable.*doesn't exist|executablePath/i,
    message: '系统没有找到可用于采集的 Microsoft Edge 浏览器。',
    suggestion: '请安装 Microsoft Edge，或设置 EDGE_PATH 指向 msedge.exe。',
  },
];

const DEFAULT_ERROR = {
  key: 'unknown',
  severity: 'error',
  title: '采集任务失败',
  message: '采集任务执行失败，暂时无法自动判断具体原因。',
  suggestion: '请展开详细日志，查看失败前后的原始错误信息。',
};

function normalizeTaskError(errorText) {
  const raw = String(errorText || '').trim();
  if (!raw) return null;
  const rule = RULES.find(item => item.pattern.test(raw)) || DEFAULT_ERROR;
  return {
    key: rule.key,
    severity: rule.severity,
    title: rule.title,
    message: rule.message,
    suggestion: rule.suggestion,
    raw,
  };
}

module.exports = {
  normalizeTaskError,
  TASK_ERROR_RULES: RULES,
};
