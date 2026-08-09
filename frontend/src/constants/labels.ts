// Single source for the constants mirroring the backend's graduated-check enums.
// CloudCheckLevel / LocalCheckLevel each used to be declared as an identical literal object in both
// api/backupConfigs.ts and api/tasks.ts — a place where two enum definitions could drift apart.
// They live here now, with both api modules re-exporting, so consumer import paths are unchanged.
//
// Note: the two number → display-text maps (cloudLevelLabels/localLevelLabels versus
// cloudCheckLabels/localCheckLabels) carry different wording for different page contexts. They are
// not duplicates and stay where they are.
export const CloudCheckLevel = { None: 0, Metadata: 1, ExistenceSize: 2, Content: 3 } as const
export const LocalCheckLevel = { None: 0, Attributes: 1, Content: 2 } as const
