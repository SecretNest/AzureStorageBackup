// 单一来源：与后端"分级检查"枚举对应的常量（§5.7 合并重复 label 字典）。
// CloudCheckLevel / LocalCheckLevel 此前在 api/backupConfigs.ts 与 api/tasks.ts 中各自
// 定义了一份完全相同的字面量对象——两处枚举定义漂移的风险点。现统一到此处，
// 两个 api 模块改为从这里 re-export，消费方 import 路径保持不变。
//
// 注：两处的"数字 → 展示文案"映射（cloudLevelLabels/localLevelLabels vs
// cloudCheckLabels/localCheckLabels）文案不同（不同页面语境），并非重复，故不合并，
// 各自保留在原文件中。
export const CloudCheckLevel = { None: 0, Metadata: 1, ExistenceSize: 2, Content: 3 } as const
export const LocalCheckLevel = { None: 0, Attributes: 1, Content: 2 } as const
