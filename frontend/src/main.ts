import { createApp } from 'vue'
import ArcoVue from '@arco-design/web-vue'
import App from './App.vue'
import '@arco-design/web-vue/dist/arco.css'
import './styles.css'

// Arco 暗色主题
document.body.setAttribute('arco-theme', 'dark')

createApp(App).use(ArcoVue).mount('#app')
