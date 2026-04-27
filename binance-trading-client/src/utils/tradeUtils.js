/**
 * src/utils/tradeUtils.ts
 * 交易核心本地计算引擎 (支持全仓/逐仓物理模型)
 */
/**
 * ⚡️ 计算强平价格 (融合全仓与逐仓模型)
 * @param pos 当前需要计算强平价的仓位
 * @param walletBalance 账户可用钱包余额 (全仓必需)
 * @param otherCrossPositions 其他全仓合约的状态 (全仓必需，用于共享保证金抵扣)
 */
export const calculateLiquidationPrice = (pos, walletBalance = 0, otherCrossPositions = []) => {
    const mmr = pos.mmr || 0.004;
    const size = Math.abs(pos.amount);
    if (pos.entryPrice <= 0 || size <= 0)
        return 0;
    // ==========================================
    // 模型 A: 逐仓模式 (Isolated) 
    // 仅计算单仓，无视钱包余额和其他仓位
    // ==========================================
    if (pos.marginType === 'isolated') {
        let liqPrice = 0;
        if (pos.side === 'LONG') {
            liqPrice = pos.entryPrice * (1 - (1 / pos.leverage) + mmr);
            if (liqPrice < 0)
                liqPrice = 0;
        }
        else {
            liqPrice = pos.entryPrice * (1 + (1 / pos.leverage) - mmr);
        }
        return Number(liqPrice.toFixed(4));
    }
    // ==========================================
    // 模型 B: 全仓模式 (Cross)
    // 融合账户余额 + 其他全仓浮盈浮亏 + 其他全仓维持保证金占用
    // ==========================================
    // E0 = 除当前仓位外，全仓账户的净权益
    let E0 = walletBalance;
    // MM0 = 除当前仓位外，其他全仓合约的维持保证金总和
    let MM0 = 0;
    otherCrossPositions.forEach(other => {
        if (other.marginType !== 'cross')
            return;
        E0 += other.unrealizedPnL;
        const otherSize = Math.abs(other.amount);
        const otherMmr = other.mmr || 0.004;
        MM0 += otherSize * other.markPrice * otherMmr;
    });
    let liqPrice = 0;
    if (pos.side === 'LONG') {
        const denominator = size * (1 - mmr);
        if (denominator <= 0)
            return 0;
        liqPrice = (size * pos.entryPrice + MM0 - E0) / denominator;
    }
    else { // SHORT
        const denominator = size * (1 + mmr);
        if (denominator <= 0)
            return 0;
        liqPrice = (size * pos.entryPrice - MM0 + E0) / denominator;
    }
    return liqPrice > 0 ? Number(liqPrice.toFixed(4)) : 0;
};
