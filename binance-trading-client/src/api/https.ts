import axios from 'axios'
import type { AxiosInstance, AxiosRequestConfig, AxiosResponse } from 'axios'


// 创建 Axios 实例
const http: AxiosInstance = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || 'http://localhost:5000/api', // 你的 C# 后端地址
  timeout: 10000, // 超时时间 10 秒
  headers: {
    'Content-Type': 'application/json'
  }
})

// 请求拦截器
http.interceptors.request.use(
  (config) => {
        // 预留：后续可以在这里注入访问后端的 Token
        // const token = localStorage.getItem('token');
        // if (token) {
        //   config.headers.Authorization = `Bearer ${token}`;
        // }
        return config
    },
    (error) => {
        return Promise.reject(error)
    }
)

// 响应拦截器
http.interceptors.response.use(
    (response: AxiosResponse) => {
        // 这里可以根据你的 C# 后端统一返回格式进行剥离
        // 例如后端统一返回 { code: 200, data: {...}, message: "success" }
        const { data } = response
        // 假设非 200 为业务报错
        if (data && data.code && data.code !== 200) {
            console.error('API 业务错误:', data.message)
            // 可以在这里触发全局的错误提示组件 (如 ElMessage 或自定义的 Toast)
            return Promise.reject(new Error(data.message || 'Error'))
        }
        return data.data || data
    },
    (error) => {
        // 处理 HTTP 状态码错误 (如 401, 404, 500)
        console.error('网络请求错误:', error.message)
        return Promise.reject(error)
    }
)

export default http