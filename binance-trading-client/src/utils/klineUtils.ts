// src/utils/klineUtils.ts
// K 线图表纯计算辅助工具函数库

// 1. 防弹级步长格式化 (规避原生 JS 浮点数精度丢失)
export const formatByStep = (value: number, stepStr: string) => {
  const step = parseFloat(stepStr);
  if (isNaN(step) || step <= 0) return value.toString();
  let dec = 0;
  if (step < 1) {
    const stepStrParsed = step.toString();
    if (stepStrParsed.includes('e-')) {
      dec = parseInt(stepStrParsed.split('e-')[1], 10);
    } else if (stepStrParsed.includes('.')) {
      dec = stepStrParsed.split('.')[1].length;
    }
  }
  const truncated = Math.floor(value / step + Number.EPSILON) * step;
  return truncated.toFixed(dec);
};

// 2. 时间戳格式化为 YYYY-MM-DD HH:mm:ss
export const formatDateTime = (timestamp: number) => {
  const date = new Date(timestamp * 1000); 
  const y = date.getFullYear(); const m = String(date.getMonth() + 1).padStart(2, '0'); const d = String(date.getDate()).padStart(2, '0');
  const H = String(date.getHours()).padStart(2, '0'); const M = String(date.getMinutes()).padStart(2, '0'); const S = String(date.getSeconds()).padStart(2, '0');
  return `${y}-${m}-${d} ${H}:${M}:${S}`;
};

// 3. 动态精度推导引擎
export const getPrecisionConfig = (symbol: string, symbolRules: any, marketTickers: any, lastPrice?: number) => {
  const rule = symbolRules[symbol];
  if (rule && rule.tickSize) {
    const minM = parseFloat(rule.tickSize);
    let dec = 2;
    if (minM < 1) {
      const str = minM.toString();
      if (str.includes('e')) {
        const match = str.match(/e-(\d+)/);
        if (match) dec = parseInt(match[1], 10);
      } else {
        dec = str.split('.')[1]?.length || 2;
      }
    } else { dec = 0; }
    return { precision: dec, minMove: minM };
  }

  const p = lastPrice || marketTickers[symbol]?.lastPrice || 100;
  if (p < 0.000001) return { precision: 8, minMove: 0.00000001 };
  if (p < 0.001) return { precision: 6, minMove: 0.000001 };
  if (p < 0.1) return { precision: 4, minMove: 0.0001 };
  if (p < 10) return { precision: 3, minMove: 0.001 };
  return { precision: 2, minMove: 0.01 };
};

// 4. Heikin-Ashi (平均K线) 计算
export const calculateHeikinAshi = (rawData: any[]) => {
  const haData = [];
  let prevHA: any = null;
  for (const raw of rawData) {
    const rawVolume = raw.value !== undefined ? raw.value : (raw.volume !== undefined ? raw.volume : (raw.vol || 0));
    const ha = { 
      time: raw.time, 
      parsedTime: raw.parsedTime,
      open: 0, high: 0, low: 0, close: 0, 
      value: rawVolume, color: raw.color 
    };
    
    ha.close = (Number(raw.open) + Number(raw.high) + Number(raw.low) + Number(raw.close)) / 4;
    if (!prevHA) { ha.open = (Number(raw.open) + Number(raw.close)) / 2; } 
    else { ha.open = (Number(prevHA.open) + Number(prevHA.close)) / 2; }
    ha.high = Math.max(Number(raw.high), ha.open, ha.close);
    ha.low = Math.min(Number(raw.low), ha.open, ha.close);
    ha.color = ha.close >= ha.open ? 'rgba(38, 166, 154, 0.5)' : 'rgba(239, 83, 80, 0.5)';
    
    haData.push(ha);
    prevHA = ha;
  }
  return haData;
};

// 5. 数学碰撞检测：点到线段的距离
export const distToSegment = (px: number, py: number, x1: number, y1: number, x2: number, y2: number) => {
  const l2 = (x1 - x2)**2 + (y1 - y2)**2; if (l2 === 0) return Math.sqrt((px - x1)**2 + (py - y1)**2);
  let t = ((px - x1) * (x2 - x1) + (py - y1) * (y2 - y1)) / l2; t = Math.max(0, Math.min(1, t)); 
  return Math.sqrt((px - (x1 + t * (x2 - x1)))**2 + (py - (y1 + t * (y2 - y1)))**2);
};

// 6. 数学碰撞检测：点到射线的距离
export const distToRay = (px: number, py: number, x1: number, y1: number, x2: number, y2: number) => {
  const l2 = (x1 - x2)**2 + (y1 - y2)**2; if (l2 === 0) return Math.sqrt((px - x1)**2 + (py - y1)**2);
  let t = ((px - x1) * (x2 - x1) + (py - y1) * (y2 - y1)) / l2; t = Math.max(0, t); 
  return Math.sqrt((px - (x1 + t * (x2 - x1)))**2 + (py - (y1 + t * (y2 - y1)))**2);
};