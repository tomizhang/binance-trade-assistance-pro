// src/utils/optimize.ts

/**
 * 防抖 (Debounce)：在最后一次操作结束后的 delay 毫秒才执行
 * 适用场景：价格/数量输入框，用户停止打字后再触发“计算最大可开仓位”、“计算手续费”。
 */
export function debounce<T extends (...args: any[]) => void>(fn: T, delay: number = 300) {
  let timeoutId: ReturnType<typeof setTimeout> | null = null;
  return function (this: any, ...args: Parameters<T>) {
    if (timeoutId) clearTimeout(timeoutId);
    timeoutId = setTimeout(() => {
      fn.apply(this, args);
    }, delay);
  };
}

/**
 * 节流 (Throttle)：在一定时间内，无论触发多少次，只执行一次
 * 适用场景：滑块拖动 (杠杆调节、仓位比例调节)，控制计算频率。
 */
export function throttle<T extends (...args: any[]) => void>(fn: T, limit: number = 300) {
  let inThrottle: boolean;
  return function (this: any, ...args: Parameters<T>) {
    if (!inThrottle) {
      fn.apply(this, args);
      inThrottle = true;
      setTimeout(() => (inThrottle = false), limit);
    }
  };
}

/**
 * 🌟 异步并发锁 (Async Lock)：专为“下单按钮”设计
 * 与节流不同，它不看时间，而是看网络请求是否返回。在上一个请求 pending 期间，后续点击一律拦截。
 */
export function createAsyncLock() {
  let isLocked = false;
  
  return async <T>(asyncFn: () => Promise<T>): Promise<T | void> => {
    if (isLocked) {
      console.warn('🔒 动作过快，请求已被并发锁拦截！');
      return;
    }
    isLocked = true;
    try {
      return await asyncFn();
    } finally {
      isLocked = false;
    }
  };
}