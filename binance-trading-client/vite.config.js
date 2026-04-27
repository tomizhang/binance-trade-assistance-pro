import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'path';
import { HttpsProxyAgent } from 'https-proxy-agent';
// 👇 替换为你本地代理软件的真实 HTTP 端口 (比如 7890, 10808, 10809 等)
const localProxyUrl = 'http://127.0.0.1:10808';
export default defineConfig({
    plugins: [vue()],
    resolve: {
        alias: {
            '@': resolve(__dirname, 'src'),
        },
    },
    server: {
        proxy: {
            '/fapi': {
                target: 'https://fapi.binance.com',
                changeOrigin: true,
                // 🌟 核心修复：强制 Vite 代理走你的本地科学上网通道
                agent: new HttpsProxyAgent(localProxyUrl),
                // 如果遇到证书问题，可以把下面这行打开
                secure: false,
            }
        }
    }
});
