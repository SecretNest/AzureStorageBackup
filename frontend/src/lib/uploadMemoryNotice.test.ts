import { describe, expect, test } from 'vitest'

import { uploadMemoryNotice, uploadMemoryShare } from './uploadMemoryNotice'

const MB = 1024 * 1024

describe('uploadMemoryShare', () => {
  test('splits the limit across a backup’s uploaders, which are the concurrency plus one', () => {
    // 600 MB, concurrency 5 → 6 uploaders → 100 MB each. Mirrors UploadMemoryBudget.PerStream on the backend.
    expect(uploadMemoryShare(600 * MB, 5)).toBe(100 * MB)
  })

  test('never goes below the 80 KB floor once a limit is set', () => {
    expect(uploadMemoryShare(1 * MB, 100)).toBe(80 * 1024)
  })

  test('zero means nothing is held, floor included', () => {
    expect(uploadMemoryShare(0, 5)).toBe(0)
  })
})

describe('uploadMemoryNotice', () => {
  test('a volume that fits its share is sent from memory in one read', () => {
    expect(uploadMemoryNotice(1024 * MB, 5, 100 * MB)).toBe(
      'Each of a backup’s 6 upload streams may hold up to 170.7 MB. A 100.0 MB volume fits, so volumes are hashed and sent from memory in one disk read.',
    )
  })

  test('a volume past its share is hashed first and read a second time for the send', () => {
    expect(uploadMemoryNotice(600 * MB, 10, 100 * MB)).toBe(
      'Each of a backup’s 11 upload streams may hold up to 54.5 MB. A 100.0 MB volume does not fit, so every volume is hashed from disk first and read a second time for the send — still labelled, just one extra read.',
    )
  })

  test('zero says plainly that nothing is ever held in memory', () => {
    expect(uploadMemoryNotice(0, 5, 100 * MB)).toBe(
      'Set to 0: no volume is ever held in memory. Every volume is hashed from disk first and read a second time for the send — still labelled, just one extra read.',
    )
  })

  test('with splitting off an archive is one volume of any size', () => {
    expect(uploadMemoryNotice(600 * MB, 5, null)).toBe(
      'Each of a backup’s 6 upload streams may hold up to 100.0 MB. Volume splitting is off, so an archive is one volume of any size: archives up to 100.0 MB are sent from memory in one disk read, larger ones are hashed first and read a second time for the send.',
    )
  })
})
