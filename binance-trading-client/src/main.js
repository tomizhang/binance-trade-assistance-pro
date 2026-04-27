import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './App.vue';
import './style.css'; // 稍后我们清空默认样式
const app = createApp(App);
app.use(createPinia());
app.mount('#app');
