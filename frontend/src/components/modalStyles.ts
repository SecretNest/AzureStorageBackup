// 弹窗共用样式。真正的样式在 index.css 的 .modal-overlay / .modal-panel，
// 这里只保留 className 常量，避免各处再手抄一次字符串。
// 原先这里硬编码 background:'#fff'，深色模式下白底配深色 token 是坏的。
export const overlayStyle = 'modal-overlay'
export const panelStyle = 'modal-panel'
