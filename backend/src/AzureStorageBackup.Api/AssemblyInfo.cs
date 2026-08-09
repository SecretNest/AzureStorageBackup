using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// The test assembly needs to drive SchedulerService.TickAsync (internal) directly to cover the keyring skip branch,
// and we do not want to make that method public purely for the tests.
[assembly: InternalsVisibleTo("AzureStorageBackup.Api.Tests")]

// This assembly only ever runs on Linux. That is a **statement of fact**, not a way to silence a warning: what we ship is an
// mcr.microsoft.com/dotnet/aspnet:10.0 (Debian) container that also depends on the official 7zz binary and on Unix permission bits,
// so on any other platform it would not get off the ground in the first place.
// Without the declaration, the handful of Unix permission APIs (File.Get/SetUnixFileMode, used to grant read permission on files we
// cannot open) would each raise a CA1416 "unsupported on windows" — around seventy of them across the whole solution. The noise itself
// has a cost: a genuinely new warning gets buried in it and goes unnoticed — which has already come close to happening in several reviews on this project.
// One thing worth spelling out: the catch in SevenZipCompressor.Grant lists only IOException and UnauthorizedAccessException,
// not PlatformNotSupportedException. On Windows that would blow straight through — and this declaration is precisely the claim that "that premise does not exist".
[assembly: SupportedOSPlatform("linux")]
