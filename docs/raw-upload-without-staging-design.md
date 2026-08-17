# Uploading a raw blob from where it already is

## The problem

When a file is stored without compression and without encryption, and fits in one volume, the pipeline
still copies it — in full — into the staging area before uploading it.

`StreamAndStageAsync` picks that route:

```csharp
var raw = storeOnly && string.IsNullOrEmpty(request.Password)
    && (request.Options.VolumeBytes is not { } vb || before.Length <= vb);
```

and `CopyRawStreamingAsync` then writes a byte-for-byte duplicate into compress-temp, which
`MoveToStaged` moves into staged-temp, from which the uploader reads it.

So a 6 GB store-only file costs **two reads and a write** — read the source while hashing, write the
copy, read the copy to upload — where the content the copy holds is identical to the source that is
still sitting there. It also books 6 GB against the staging quota, which is process-wide: while that
copy exists, every backup on the machine has that much less room to compress into.

## Starting point

- **The copy is what fixes the content.** The hash is computed *while* copying (`HashingStream`), so the
  bytes hashed and the bytes stored are the same set by construction. That property is why the copy is
  there, and any change has to preserve it or replace it with something equally strong.
- **The blob address is the content.** A data blob is stored at `data/{fullHash}`. If the bytes that
  reach the cloud are not the bytes that produced the hash, the container holds an object whose name
  contradicts its content — a corruption no restore would detect until it failed.
- **The uploader takes a path.** `IBlobUploader.UploadIfMissingAsync(..., string filePath, ...)` opens the
  file itself, so "upload from the source" needs no new interface.
- **The pipeline already carries entries that own no staged archive.** `PendingUpload.Handoff` is nullable
  and a probe hit already travels with it null, so a raw item that stages nothing needs no new shape.
- **The pack path already re-verifies after producing.** `CompressGroupAsync` stats every member after
  compressing and rehashes any whose metadata moved. There is an established pattern here for "prove the
  source did not change under us", and it is metadata-first.

## Design

### 1. Hash the source, then upload the source

The raw route stops copying. It reads the file once to compute the content identity, notes its metadata,
and hands the uploader the **source path**:

```
stat → read once (hash only, no copy) → resolve the blob address → upload from the source path
     → stat again → unchanged? done.  changed? undo and fall back.
```

Two reads, no write, and nothing charged to the staging quota.

### 2. The guard: stat before, stat after, and undo if it moved

The window the copy used to close is "the source changes between being hashed and being uploaded". It is
closed here by bracketing the upload with the same metadata test the diff uses to decide a file is
unchanged — length and last-write time — and treating any movement as a failure of that upload:

```csharp
// Same test the diff trusts to call a file unchanged (BackupDiffer.cs:195-201), applied to a much
// shorter window: there, minutes may pass between the scan and the read; here, only the upload itself.
```

If the metadata moved, the blob that was just written is **deleted** — it is named for a hash its content
may no longer match, and leaving it would be worse than not having uploaded at all — and the item is
retried through the copying route, which is immune because it uploads a snapshot. One retry, then the
existing transient-error machinery takes over.

The asymmetry is deliberate: the fast path is optimistic and verified, the fallback is pessimistic and
unconditional. A file being rewritten during its own backup is rare; paying a copy for every raw file to
pre-empt it is not worth it, and never noticing would be unacceptable.

**The test is applied on both endings of the upload, not only on the returning one.** "The upload threw"
does not mean "nothing was committed": Azure acknowledges a commit over a connection that can die on the way
back, which is the routine NAS-to-Azure blip — the SDK's retries exhaust and what surfaces is a status-0
error with the object already in the container. An upload that ends in an exception therefore runs the same
stat-and-take-back before the exception continues on its way. Only when the source moved: an upload that
failed with the file exactly as it was hashed leaves nothing that cannot be vouched for, and deleting it
regardless would throw away a correct object the retry's if-missing upload would otherwise skip — a full
re-upload of a multi-GB file, bought for nothing, on the failure that happens most often.

That matters because of what an orphan of this kind turns into. The retry re-hashes the moved source, so it
lands on a *different* address and the first object is orphaned rather than overwritten; nothing sweeps it
afterwards (the in-flight purge runs for Stop now alone, the closing orphan sweep only for a round that
adopted or voided a journal or is its config's first on the container). A later run that legitimately
produces that same hash then claims the address, finds the single-volume path clearing nothing, and is told
"already there" by the if-missing upload without a byte being read — so the index records `data/{H}` as
holding `H`, and it does not.

**A take-back that cannot be done is said out loud.** The delete is retried under the same bounded policy
the cleanup path uses for its point operations, and if it still fails the address goes into the operation
log and out through the `UnrecoverableError` channel, naming the blob and the file. Nothing else in the
system will ever find that object: check and restore both read the index, the index agrees with the name,
and the name is the only thing about it that is wrong. The in-flight registration is deliberately left
standing in that case, so a Stop now still clears the address.

### 3. What stays exactly as it is

Encrypted, compressed, or split blobs keep copying — their stored bytes are not the source bytes, so
there is nothing to upload in place. Packs are unaffected. The journal, the index and the dedup tables
see the same values either way: the record describes content, and the content is identical.

## What this does not do

**It does not remove the second read.** The upload reads the file again; only the write and the second
read *of the copy* go away. Eliminating that would mean hashing and uploading in one pass, which the
uploader's path-based interface does not offer and which would trade a much larger change for a smaller
saving.

**It does not close the rewrite window absolutely.** A file rewritten during its upload *and* restored to
its previous length and mtime would pass the guard. That is the same boundary the diff has lived with
since the beginning, now applied over a window of seconds rather than minutes.

**It does not apply when the file needs splitting.** A volume-split raw file has more than one output
object, and those objects do not exist until 7z writes them.

## Tests

- **A raw upload leaves nothing in staging.** After backing up a store-only unencrypted file, the staged
  pool returned to zero *and* never rose above zero during the run — assert on the peak, not the end
  state, since the end state is zero either way.
- **The uploaded bytes are the source bytes.** Round-trip a raw blob and compare it to the original.
- **A file rewritten during its upload does not produce a mismatched blob.** Force the window with a
  blocking uploader: begin the upload, rewrite the source, release. The run must not leave a
  `data/{hash}` whose content hashes differently — either it retried through the copy or the object is
  absent.
- **The same, on an upload that commits and then fails.** The uploader parks, is let through to the real
  one so the object is genuinely written, and only then reports a status-0 error. Run without a run control,
  so the injected error is not retried and nothing but the code under test can have touched the container:
  afterwards it must hold no data object at all.
- **A take-back that cannot be done names the address.** Sabotage only the orchestrator's own view of the
  container, from the instant the source is rewritten. The run survives (the copying route is immune), the
  object is still there, and the operation log holds an error naming it and the file it came from.
- **Encrypted and compressed blobs still copy.** The route is chosen by the same predicate as before;
  pin it so an over-eager future edit cannot send an encrypted blob's plaintext.
